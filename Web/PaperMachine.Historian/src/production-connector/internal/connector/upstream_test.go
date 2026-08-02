package connector

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const validPayload = `{"produzindo":true,"idmapaproducao":8639,"op":1197,"itens":[{"codproduto":"MIOLO"}]}`

func tls13TestClient(t *testing.T, handler http.Handler) (*httptest.Server, *UpstreamClient) {
	t.Helper()
	server := httptest.NewUnstartedServer(handler)
	server.EnableHTTP2 = true
	server.StartTLS()
	t.Cleanup(server.Close)

	pool := x509.NewCertPool()
	pool.AddCert(server.Certificate())
	config := DefaultConfig()
	config.UpstreamURL = server.URL
	config.AllowedUpstreamHost = "127.0.0.1"
	return server, newUpstreamClient(config, pool)
}

func TestFetchRequiresTLS13AndValidatesPayload(t *testing.T) {
	_, client := tls13TestClient(t, http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		if request.Header.Get("x-api-key") != "secret" {
			t.Error("API key header was not forwarded")
		}
		response.Header().Set("Content-Type", "application/json")
		_, _ = response.Write([]byte(validPayload))
	}))
	body, summary, err := client.Fetch(context.Background(), "secret")
	if err != nil {
		t.Fatalf("fetch failed: %v", err)
	}
	if !strings.Contains(string(body), `"idmapaproducao":8639`) || summary.ProductionMapID != "8639" || summary.ProductionOrder != "1197" || !summary.IsProducing || summary.ItemCount != 1 {
		t.Fatalf("unexpected validated response: %+v", summary)
	}
}

func TestFetchRejectsRedirect(t *testing.T) {
	_, client := tls13TestClient(t, http.HandlerFunc(func(response http.ResponseWriter, _ *http.Request) {
		http.Redirect(response, &http.Request{}, "https://example.com", http.StatusFound)
	}))
	if _, _, err := client.Fetch(context.Background(), "secret"); err == nil || !strings.Contains(err.Error(), "redirects are forbidden") {
		t.Fatalf("expected redirect rejection, got %v", err)
	}
}

func TestFetchRejectsTLS12OnlyServer(t *testing.T) {
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(response http.ResponseWriter, _ *http.Request) {
		_, _ = response.Write([]byte(validPayload))
	}))
	server.TLS = &tls.Config{MaxVersion: tls.VersionTLS12}
	server.StartTLS()
	t.Cleanup(server.Close)

	pool := x509.NewCertPool()
	pool.AddCert(server.Certificate())
	config := DefaultConfig()
	config.UpstreamURL = server.URL
	config.AllowedUpstreamHost = "127.0.0.1"
	client := newUpstreamClient(config, pool)
	if _, _, err := client.Fetch(context.Background(), "secret"); err == nil {
		t.Fatal("expected TLS 1.2-only server to be rejected")
	}
}

func TestFetchRejectsOversizedResponse(t *testing.T) {
	_, client := tls13TestClient(t, http.HandlerFunc(func(response http.ResponseWriter, _ *http.Request) {
		_, _ = response.Write([]byte(strings.Repeat("x", 2048)))
	}))
	client.config.MaximumResponseBytes = 1024
	if _, _, err := client.Fetch(context.Background(), "secret"); err == nil || !strings.Contains(err.Error(), "exceeded") {
		t.Fatalf("expected size rejection, got %v", err)
	}
}

func TestValidatePayloadRejectsMissingFields(t *testing.T) {
	if _, err := validatePayload([]byte(`{"produzindo":true}`)); err == nil {
		t.Fatal("expected incomplete payload to be rejected")
	}
}
