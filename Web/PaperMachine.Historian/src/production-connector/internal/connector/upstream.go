package connector

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
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

type WeightReceipt struct {
	EventID string  `json:"eventId"`
	Applied *bool   `json:"aplicado"`
	Weight  float64 `json:"peso"`
}

type weightPayload struct {
	EventID         string  `json:"eventId"`
	EventType       string  `json:"eventType"`
	Revision        int     `json:"revision"`
	MachineID       string  `json:"machineId"`
	CapturedAtUTC   string  `json:"capturedAtUtc"`
	WeightKg        float64 `json:"weightKg"`
	ProductionMapID string  `json:"productionMapId"`
	ProductionOrder *string `json:"productionOrder"`
	CaptureStatus   int     `json:"captureStatus"`
}

type UpstreamHTTPError struct {
	StatusCode int
}

func (e *UpstreamHTTPError) Error() string {
	return fmt.Sprintf("upstream returned HTTP %d", e.StatusCode)
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
		return nil, ProductionSummary{}, &UpstreamHTTPError{StatusCode: response.StatusCode}
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

func (c *UpstreamClient) SendWeight(
	ctx context.Context,
	apiKey string,
	idempotencyKey string,
	body []byte,
) ([]byte, WeightReceipt, error) {
	if strings.TrimSpace(apiKey) == "" {
		return nil, WeightReceipt{}, errors.New("ERP API key is empty")
	}
	if int64(len(body)) > c.config.MaximumRequestBytes {
		return nil, WeightReceipt{}, errors.New("weight request exceeded configured limit")
	}
	payload, err := validateWeightPayload(body)
	if err != nil {
		return nil, WeightReceipt{}, err
	}
	if strings.TrimSpace(idempotencyKey) == "" || idempotencyKey != payload.EventID {
		return nil, WeightReceipt{}, errors.New("idempotency key does not match eventId")
	}

	request, err := http.NewRequestWithContext(
		ctx,
		http.MethodPost,
		c.config.WeightUpstreamURL,
		bytes.NewReader(body))
	if err != nil {
		return nil, WeightReceipt{}, fmt.Errorf("create weight upstream request: %w", err)
	}
	request.Header.Set(c.config.APIKeyHeaderName, strings.TrimSpace(apiKey))
	request.Header.Set("Idempotency-Key", idempotencyKey)
	request.Header.Set("Accept", "application/json")
	request.Header.Set("Content-Type", "application/json; charset=utf-8")
	request.Header.Set("User-Agent", "CPNTeck-ProductionConnector/1")

	response, err := c.client.Do(request)
	if err != nil {
		return nil, WeightReceipt{}, fmt.Errorf("weight upstream HTTPS request failed: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode < http.StatusOK || response.StatusCode >= http.StatusMultipleChoices {
		return nil, WeightReceipt{}, &UpstreamHTTPError{StatusCode: response.StatusCode}
	}

	responseBody, err := readLimitedBody(response.Body, c.config.MaximumResponseBytes)
	if err != nil {
		return nil, WeightReceipt{}, err
	}
	var receipt WeightReceipt
	if err := json.Unmarshal(responseBody, &receipt); err != nil {
		return nil, WeightReceipt{}, fmt.Errorf("weight upstream response is not valid JSON: %w", err)
	}
	if receipt.EventID != payload.EventID || receipt.Applied == nil ||
		math.IsNaN(receipt.Weight) || math.IsInf(receipt.Weight, 0) {
		return nil, WeightReceipt{}, errors.New("weight upstream response does not confirm the submitted event")
	}
	return responseBody, receipt, nil
}

func (c *UpstreamClient) SendWeightUsingKeyFile(
	ctx context.Context,
	keyPath string,
	idempotencyKey string,
	body []byte,
) ([]byte, WeightReceipt, error) {
	key, err := readSecret(keyPath)
	if err != nil {
		return nil, WeightReceipt{}, fmt.Errorf("read ERP API key: %w", err)
	}
	return c.SendWeight(ctx, key, idempotencyKey, body)
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

func validateWeightPayload(body []byte) (weightPayload, error) {
	var payload weightPayload
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&payload); err != nil {
		return weightPayload{}, fmt.Errorf("weight request is not valid JSON: %w", err)
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		return weightPayload{}, errors.New("weight request must contain exactly one JSON document")
	}
	if payload.EventID == "" || len(payload.EventID) > 160 ||
		payload.MachineID == "" || len(payload.MachineID) > 32 ||
		!validIdentifier(payload.MachineID, false) ||
		!validIdentifier(payload.EventID, true) ||
		!strings.HasPrefix(payload.EventID, payload.MachineID+":") ||
		payload.Revision <= 0 ||
		payload.ProductionMapID == "" ||
		!isAllowedWeightEventType(payload.EventType) ||
		math.IsNaN(payload.WeightKg) || math.IsInf(payload.WeightKg, 0) ||
		payload.WeightKg <= 0 || payload.WeightKg > 100_000 {
		return weightPayload{}, errors.New("weight request contains invalid fields")
	}
	if _, err := time.Parse(time.RFC3339, payload.CapturedAtUTC); err != nil ||
		!strings.HasSuffix(payload.CapturedAtUTC, "Z") {
		return weightPayload{}, errors.New("capturedAtUtc must be an RFC3339 UTC timestamp")
	}
	return payload, nil
}

func isAllowedWeightEventType(value string) bool {
	return value == "captured" || value == "corrected" || value == "voided"
}

func validIdentifier(value string, allowColon bool) bool {
	for _, character := range value {
		if (character >= 'a' && character <= 'z') ||
			(character >= 'A' && character <= 'Z') ||
			(character >= '0' && character <= '9') ||
			character == '-' || character == '_' || (allowColon && character == ':') {
			continue
		}
		return false
	}
	return value != ""
}

func readLimitedBody(reader io.Reader, maximumBytes int64) ([]byte, error) {
	limited := io.LimitReader(reader, maximumBytes+1)
	body, err := io.ReadAll(limited)
	if err != nil {
		return nil, fmt.Errorf("read upstream response: %w", err)
	}
	if int64(len(body)) > maximumBytes {
		return nil, errors.New("upstream response exceeded configured limit")
	}
	return body, nil
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
