package connector

import (
	"path/filepath"
	"testing"
)

func TestDefaultConfigIsLoopbackAndValid(t *testing.T) {
	config := DefaultConfig()
	if err := config.Validate(); err != nil {
		t.Fatalf("default configuration should be valid: %v", err)
	}
	if config.ListenAddress != "127.0.0.1:5091" {
		t.Fatalf("unexpected listen address: %s", config.ListenAddress)
	}
	if !filepath.IsAbs(config.APIKeyFilePath) || !filepath.IsAbs(config.ClientTokenFilePath) {
		t.Fatal("secret paths must be absolute")
	}
}

func TestConfigRejectsNonLoopbackListener(t *testing.T) {
	config := DefaultConfig()
	config.ListenAddress = "0.0.0.0:5091"
	if err := config.Validate(); err == nil {
		t.Fatal("expected non-loopback listener to be rejected")
	}
}

func TestConfigRejectsUnexpectedUpstream(t *testing.T) {
	tests := []struct {
		name string
		url  string
		host string
	}{
		{name: "plain HTTP", url: "http://api.papersystem.com.br/path", host: defaultUpstreamHost},
		{name: "host mismatch", url: "https://example.com/path", host: defaultUpstreamHost},
		{name: "embedded credentials", url: "https://user:password@api.papersystem.com.br/path", host: defaultUpstreamHost},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			config := DefaultConfig()
			config.UpstreamURL = test.url
			config.AllowedUpstreamHost = test.host
			if err := config.Validate(); err == nil {
				t.Fatal("expected unsafe upstream to be rejected")
			}
		})
	}
}
