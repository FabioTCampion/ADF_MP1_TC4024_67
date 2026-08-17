package connector

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"mime"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

type Gateway struct {
	config   Config
	upstream *UpstreamClient
	logger   *slog.Logger
	fetchMu  sync.Mutex
	cacheMu  sync.RWMutex
	cache    cachedResponse
}

type cachedResponse struct {
	body      []byte
	summary   ProductionSummary
	expiresAt time.Time
}

func NewGateway(config Config, logger *slog.Logger) *Gateway {
	return &Gateway{config: config, upstream: NewUpstreamClient(config), logger: logger}
}

func (g *Gateway) Run(ctx context.Context) error {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", g.handleHealth)
	mux.HandleFunc("/v1/production/current", g.handleCurrentProduction)
	mux.HandleFunc("/v1/production/weights", g.handleWeight)
	server := &http.Server{
		Addr:              g.config.ListenAddress,
		Handler:           g.securityHeaders(mux),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       10 * time.Second,
		WriteTimeout:      g.config.RequestTimeout() + 5*time.Second,
		IdleTimeout:       30 * time.Second,
		MaxHeaderBytes:    16 * 1024,
	}
	listener, err := net.Listen("tcp", g.config.ListenAddress)
	if err != nil {
		return fmt.Errorf("listen on loopback: %w", err)
	}
	g.logger.Info("production connector started", "listenAddress", g.config.ListenAddress)

	serveErrors := make(chan error, 1)
	go func() {
		if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
			serveErrors <- err
		}
		close(serveErrors)
	}()

	select {
	case <-ctx.Done():
		shutdownContext, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := server.Shutdown(shutdownContext); err != nil {
			return fmt.Errorf("shutdown local HTTP server: %w", err)
		}
		g.logger.Info("production connector stopped")
		return nil
	case err := <-serveErrors:
		return err
	}
}

func (g *Gateway) handleWeight(response http.ResponseWriter, request *http.Request) {
	correlationID := correlationID(request)
	if request.Method != http.MethodPost {
		response.Header().Set("Allow", http.MethodPost)
		writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	if !g.authorized(request) {
		g.logger.Warn("local weight request rejected", "correlationId", correlationID)
		writeError(response, http.StatusUnauthorized, "unauthorized")
		return
	}
	mediaType, _, err := mime.ParseMediaType(request.Header.Get("Content-Type"))
	if err != nil || mediaType != "application/json" {
		writeError(response, http.StatusUnsupportedMediaType, "json_required")
		return
	}

	request.Body = http.MaxBytesReader(response, request.Body, g.config.MaximumRequestBytes)
	body, err := io.ReadAll(request.Body)
	if err != nil {
		writeError(response, http.StatusRequestEntityTooLarge, "request_too_large")
		return
	}
	payload, err := validateWeightPayload(body)
	if err != nil {
		g.logger.Warn("local weight payload rejected", "correlationId", correlationID, "error", safeError(err))
		writeError(response, http.StatusBadRequest, "invalid_weight_payload")
		return
	}
	idempotencyKey := strings.TrimSpace(request.Header.Get("Idempotency-Key"))
	if idempotencyKey == "" || idempotencyKey != payload.EventID {
		writeError(response, http.StatusBadRequest, "invalid_idempotency_key")
		return
	}

	upstreamBody, receipt, err := g.upstream.SendWeightUsingKeyFile(
		request.Context(),
		g.config.APIKeyFilePath,
		idempotencyKey,
		body)
	if err != nil {
		status := weightGatewayErrorStatus(err)
		g.logger.Warn(
			"PaperSystem weight request failed",
			"correlationId", correlationID,
			"eventId", payload.EventID,
			"error", safeError(err))
		writeError(response, status, "weight_upstream_rejected")
		return
	}

	response.Header().Set("Content-Type", "application/json; charset=utf-8")
	response.Header().Set("X-Correlation-ID", correlationID)
	response.WriteHeader(http.StatusOK)
	_, _ = response.Write(upstreamBody)
	g.logger.Info(
		"weight event delivered to PaperSystem",
		"correlationId", correlationID,
		"eventId", receipt.EventID,
		"eventType", payload.EventType,
		"revision", payload.Revision)
}

func weightGatewayErrorStatus(err error) int {
	var upstreamError *UpstreamHTTPError
	if !errors.As(err, &upstreamError) {
		return http.StatusBadGateway
	}
	switch upstreamError.StatusCode {
	case http.StatusBadRequest,
		http.StatusNotFound,
		http.StatusConflict,
		http.StatusUnprocessableEntity:
		return upstreamError.StatusCode
	case http.StatusTooManyRequests:
		return http.StatusServiceUnavailable
	default:
		return http.StatusBadGateway
	}
}

func (g *Gateway) handleHealth(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		response.Header().Set("Allow", http.MethodGet)
		writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	writeJSON(response, http.StatusOK, map[string]any{
		"status":     "ok",
		"service":    "CPNTeck Production Connector",
		"serverTime": time.Now().UTC(),
	})
}

func (g *Gateway) handleCurrentProduction(response http.ResponseWriter, request *http.Request) {
	correlationID := correlationID(request)
	if request.Method != http.MethodGet {
		response.Header().Set("Allow", http.MethodGet)
		writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	if !g.authorized(request) {
		g.logger.Warn("local connector request rejected", "correlationId", correlationID)
		writeError(response, http.StatusUnauthorized, "unauthorized")
		return
	}

	body, summary, cached, err := g.current(request.Context())
	if err != nil {
		g.logger.Warn("ERP request failed", "correlationId", correlationID, "error", safeError(err))
		writeError(response, http.StatusBadGateway, "upstream_unavailable")
		return
	}
	response.Header().Set("Content-Type", "application/json; charset=utf-8")
	response.Header().Set("X-Correlation-ID", correlationID)
	response.Header().Set("X-CPNTeck-Cache", map[bool]string{true: "HIT", false: "MISS"}[cached])
	response.WriteHeader(http.StatusOK)
	_, _ = response.Write(body)
	g.logger.Info(
		"ERP production context delivered",
		"correlationId", correlationID,
		"productionMapId", summary.ProductionMapID,
		"isProducing", summary.IsProducing,
		"itemCount", summary.ItemCount,
		"cached", cached)
}

func (g *Gateway) current(ctx context.Context) ([]byte, ProductionSummary, bool, error) {
	now := time.Now()
	g.cacheMu.RLock()
	if len(g.cache.body) > 0 && now.Before(g.cache.expiresAt) {
		body := append([]byte(nil), g.cache.body...)
		summary := g.cache.summary
		g.cacheMu.RUnlock()
		return body, summary, true, nil
	}
	g.cacheMu.RUnlock()

	g.fetchMu.Lock()
	defer g.fetchMu.Unlock()
	g.cacheMu.RLock()
	if len(g.cache.body) > 0 && time.Now().Before(g.cache.expiresAt) {
		body := append([]byte(nil), g.cache.body...)
		summary := g.cache.summary
		g.cacheMu.RUnlock()
		return body, summary, true, nil
	}
	g.cacheMu.RUnlock()

	body, summary, err := g.upstream.FetchUsingKeyFile(ctx, g.config.APIKeyFilePath)
	if err != nil {
		return nil, ProductionSummary{}, false, err
	}
	g.cacheMu.Lock()
	g.cache = cachedResponse{
		body:      append([]byte(nil), body...),
		summary:   summary,
		expiresAt: time.Now().Add(g.config.CacheTTL()),
	}
	g.cacheMu.Unlock()
	return body, summary, false, nil
}

func (g *Gateway) authorized(request *http.Request) bool {
	expected, err := readSecret(g.config.ClientTokenFilePath)
	if err != nil {
		return false
	}
	provided := strings.TrimSpace(request.Header.Get(g.config.ClientTokenHeaderName))
	if len(provided) != len(expected) || len(provided) == 0 {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(provided), []byte(expected)) == 1
}

func (g *Gateway) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Cache-Control", "no-store")
		response.Header().Set("X-Content-Type-Options", "nosniff")
		response.Header().Set("Referrer-Policy", "no-referrer")
		next.ServeHTTP(response, request)
	})
}

func correlationID(request *http.Request) string {
	provided := request.Header.Get("X-Correlation-ID")
	if len(provided) >= 8 && len(provided) <= 64 {
		valid := true
		for _, character := range provided {
			if (character >= 'a' && character <= 'z') ||
				(character >= 'A' && character <= 'Z') ||
				(character >= '0' && character <= '9') || character == '-' {
				continue
			}
			valid = false
			break
		}
		if valid {
			return provided
		}
	}
	buffer := make([]byte, 16)
	if _, err := rand.Read(buffer); err != nil {
		return fmt.Sprintf("fallback-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(buffer)
}

func safeError(err error) string {
	message := strings.ReplaceAll(strings.ReplaceAll(err.Error(), "\r", " "), "\n", " ")
	if len(message) > 300 {
		return message[:300]
	}
	return message
}

func writeError(response http.ResponseWriter, status int, code string) {
	writeJSON(response, status, map[string]string{"error": code})
}

func writeJSON(response http.ResponseWriter, status int, value any) {
	response.Header().Set("Content-Type", "application/json; charset=utf-8")
	response.WriteHeader(status)
	_ = json.NewEncoder(response).Encode(value)
}
