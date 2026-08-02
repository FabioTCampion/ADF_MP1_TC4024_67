package connector

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"
)

type ProductionSummary struct {
	ProductionMapID string `json:"productionMapId"`
	ProductionOrder string `json:"productionOrder"`
	IsProducing     bool   `json:"isProducing"`
	ItemCount       int    `json:"itemCount"`
}

type upstreamPayload struct {
	IsProducing     *bool              `json:"produzindo"`
	ProductionMapID json.RawMessage    `json:"idmapaproducao"`
	ProductionOrder json.RawMessage    `json:"op"`
	Items           *[]json.RawMessage `json:"itens"`
}

type UpstreamClient struct {
	config Config
	client *http.Client
}

func NewUpstreamClient(config Config) *UpstreamClient {
	return newUpstreamClient(config, nil)
}

func newUpstreamClient(config Config, roots *x509.CertPool) *UpstreamClient {
	dialer := &net.Dialer{Timeout: config.RequestTimeout(), KeepAlive: 30 * time.Second}
	transport := &http.Transport{
		Proxy:               nil,
		DialContext:         dialer.DialContext,
		ForceAttemptHTTP2:   true,
		TLSHandshakeTimeout: config.RequestTimeout(),
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS13,
			RootCAs:    roots,
		},
		IdleConnTimeout: 90 * time.Second,
	}
	return &UpstreamClient{
		config: config,
		client: &http.Client{
			Transport: transport,
			Timeout:   config.RequestTimeout(),
			CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
				return errors.New("upstream redirects are forbidden")
			},
		},
	}
}

func (c *UpstreamClient) Fetch(ctx context.Context, apiKey string) ([]byte, ProductionSummary, error) {
	if strings.TrimSpace(apiKey) == "" {
		return nil, ProductionSummary{}, errors.New("ERP API key is empty")
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, c.config.UpstreamURL, nil)
	if err != nil {
		return nil, ProductionSummary{}, fmt.Errorf("create upstream request: %w", err)
	}
	request.Header.Set(c.config.APIKeyHeaderName, strings.TrimSpace(apiKey))
	request.Header.Set("Accept", "application/json")
	request.Header.Set("User-Agent", "CPNTeck-ProductionConnector/1")

	response, err := c.client.Do(request)
	if err != nil {
		return nil, ProductionSummary{}, fmt.Errorf("upstream HTTPS request failed: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return nil, ProductionSummary{}, fmt.Errorf("upstream returned HTTP %d", response.StatusCode)
	}

	limited := io.LimitReader(response.Body, c.config.MaximumResponseBytes+1)
	body, err := io.ReadAll(limited)
	if err != nil {
		return nil, ProductionSummary{}, fmt.Errorf("read upstream response: %w", err)
	}
	if int64(len(body)) > c.config.MaximumResponseBytes {
		return nil, ProductionSummary{}, errors.New("upstream response exceeded configured limit")
	}
	summary, err := validatePayload(body)
	if err != nil {
		return nil, ProductionSummary{}, err
	}
	return body, summary, nil
}

func (c *UpstreamClient) FetchUsingKeyFile(ctx context.Context, path string) ([]byte, ProductionSummary, error) {
	key, err := readSecret(path)
	if err != nil {
		return nil, ProductionSummary{}, fmt.Errorf("read ERP API key: %w", err)
	}
	return c.Fetch(ctx, key)
}

func validatePayload(body []byte) (ProductionSummary, error) {
	var payload upstreamPayload
	if err := json.Unmarshal(body, &payload); err != nil {
		return ProductionSummary{}, fmt.Errorf("upstream response is not valid JSON: %w", err)
	}
	if payload.IsProducing == nil || payload.Items == nil || len(payload.ProductionMapID) == 0 {
		return ProductionSummary{}, errors.New("upstream JSON is missing produzindo, idmapaproducao or itens")
	}
	mapID, err := scalarText(payload.ProductionMapID)
	if err != nil || mapID == "" || mapID == "0" {
		return ProductionSummary{}, errors.New("upstream JSON contains an invalid idmapaproducao")
	}
	order := ""
	if len(payload.ProductionOrder) > 0 && string(payload.ProductionOrder) != "null" {
		order, err = scalarText(payload.ProductionOrder)
		if err != nil {
			return ProductionSummary{}, errors.New("upstream JSON contains an invalid op")
		}
	}
	return ProductionSummary{
		ProductionMapID: mapID,
		ProductionOrder: order,
		IsProducing:     *payload.IsProducing,
		ItemCount:       len(*payload.Items),
	}, nil
}

func scalarText(value json.RawMessage) (string, error) {
	var text string
	if err := json.Unmarshal(value, &text); err == nil {
		return strings.TrimSpace(text), nil
	}
	var number json.Number
	decoder := json.NewDecoder(strings.NewReader(string(value)))
	decoder.UseNumber()
	if err := decoder.Decode(&number); err != nil {
		return "", err
	}
	if _, err := strconv.ParseFloat(number.String(), 64); err != nil {
		return "", err
	}
	return number.String(), nil
}

func readSecret(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	if len(data) > 4096 {
		return "", errors.New("secret file is larger than 4096 bytes")
	}
	secret := strings.TrimSpace(string(data))
	if secret == "" {
		return "", errors.New("secret file is empty")
	}
	return secret, nil
}
