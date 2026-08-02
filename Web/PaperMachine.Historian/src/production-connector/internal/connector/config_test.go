package connector

import (
	"encoding/json"
	"os"
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

func TestLoadConfigAcceptsUtf8BomFromWindowsPowerShell(t *testing.T) {
	config := DefaultConfig()
	data, err := json.Marshal(config)
	if err != nil {
		t.Fatalf("marshal config: %v", err)
	}

	path := filepath.Join(t.TempDir(), "config.json")
	data = append([]byte{0xEF, 0xBB, 0xBF}, data...)
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("write config: %v", err)
	}

	loaded, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("load config with UTF-8 BOM: %v", err)
	}
	if loaded.ListenAddress != config.ListenAddress {
		t.Fatalf("listen address = %q, want %q", loaded.ListenAddress, config.ListenAddress)
	}
}
