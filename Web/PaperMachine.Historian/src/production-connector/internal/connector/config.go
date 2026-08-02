package connector

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

const (
	defaultUpstreamURL  = "https://api.papersystem.com.br/apontamentos/cpnteck/jupia/mp"
	defaultUpstreamHost = "api.papersystem.com.br"
)

type Config struct {
	ListenAddress         string `json:"listenAddress"`
	UpstreamURL           string `json:"upstreamUrl"`
	AllowedUpstreamHost   string `json:"allowedUpstreamHost"`
	APIKeyHeaderName      string `json:"apiKeyHeaderName"`
	APIKeyFilePath        string `json:"apiKeyFilePath"`
	ClientTokenHeaderName string `json:"clientTokenHeaderName"`
	ClientTokenFilePath   string `json:"clientTokenFilePath"`
	RequestTimeoutSeconds int    `json:"requestTimeoutSeconds"`
	CacheTTLSeconds       int    `json:"cacheTtlSeconds"`
	MaximumResponseBytes  int64  `json:"maximumResponseBytes"`
}

func DefaultConfigPath() string {
	if runtime.GOOS == "windows" {
		root := os.Getenv("ProgramData")
		if root == "" {
			root = `C:\ProgramData`
		}
		return filepath.Join(root, "CPNTeck", "ProductionConnector", "config.json")
	}
	return "/etc/cpnteck/production-connector/config.json"
}

func DefaultConfig() Config {
	var dataRoot string
	if runtime.GOOS == "windows" {
		root := os.Getenv("ProgramData")
		if root == "" {
			root = `C:\ProgramData`
		}
		dataRoot = filepath.Join(root, "CPNTeck", "ProductionConnector")
	} else {
		dataRoot = "/var/lib/cpnteck/production-connector"
	}
	return Config{
		ListenAddress:         "127.0.0.1:5091",
		UpstreamURL:           defaultUpstreamURL,
		AllowedUpstreamHost:   defaultUpstreamHost,
		APIKeyHeaderName:      "x-api-key",
		APIKeyFilePath:        filepath.Join(dataRoot, "secrets", "erp-api-key.txt"),
		ClientTokenHeaderName: "x-cpnteck-connector-token",
		ClientTokenFilePath:   filepath.Join(dataRoot, "secrets", "historian-token.txt"),
		RequestTimeoutSeconds: 10,
		CacheTTLSeconds:       55,
		MaximumResponseBytes:  1_048_576,
	}
}

func LoadConfig(path string) (Config, error) {
	config := DefaultConfig()
	data, err := os.ReadFile(filepath.Clean(path))
	if err != nil {
		return Config{}, fmt.Errorf("read configuration: %w", err)
	}
	// Windows PowerShell 5.1 writes a UTF-8 BOM when Set-Content is used with
	// -Encoding UTF8. Accept it so configurations created on older Windows LTSC
	// installations remain portable and valid.
	data = bytes.TrimPrefix(data, []byte{0xEF, 0xBB, 0xBF})
	if err := json.Unmarshal(data, &config); err != nil {
		return Config{}, fmt.Errorf("parse configuration: %w", err)
	}
	if err := config.Validate(); err != nil {
		return Config{}, err
	}
	return config, nil
}

func (c Config) Validate() error {
	host, _, err := net.SplitHostPort(c.ListenAddress)
	if err != nil {
		return fmt.Errorf("listenAddress must contain an IP and port: %w", err)
	}
	ip := net.ParseIP(host)
	if ip == nil || !ip.IsLoopback() {
		return errors.New("listenAddress must use a loopback IP")
	}
	upstream, err := url.Parse(c.UpstreamURL)
	if err != nil || upstream.Scheme != "https" || upstream.Host == "" || upstream.User != nil {
		return errors.New("upstreamUrl must be an absolute HTTPS URL without credentials")
	}
	if !strings.EqualFold(upstream.Hostname(), c.AllowedUpstreamHost) {
		return errors.New("upstreamUrl host does not match allowedUpstreamHost")
	}
	if upstream.Fragment != "" {
		return errors.New("upstreamUrl must not contain a fragment")
	}
	if !validHeaderName(c.APIKeyHeaderName) || !validHeaderName(c.ClientTokenHeaderName) {
		return errors.New("configured HTTP header name is invalid")
	}
	if !filepath.IsAbs(c.APIKeyFilePath) || !filepath.IsAbs(c.ClientTokenFilePath) {
		return errors.New("secret file paths must be absolute")
	}
	if c.RequestTimeoutSeconds < 2 || c.RequestTimeoutSeconds > 60 {
		return errors.New("requestTimeoutSeconds must be between 2 and 60")
	}
	if c.CacheTTLSeconds < 0 || c.CacheTTLSeconds > 300 {
		return errors.New("cacheTtlSeconds must be between 0 and 300")
	}
	if c.MaximumResponseBytes < 1_024 || c.MaximumResponseBytes > 10_485_760 {
		return errors.New("maximumResponseBytes must be between 1024 and 10485760")
	}
	return nil
}

func (c Config) RequestTimeout() time.Duration {
	return time.Duration(c.RequestTimeoutSeconds) * time.Second
}

func (c Config) CacheTTL() time.Duration {
	return time.Duration(c.CacheTTLSeconds) * time.Second
}

func validHeaderName(value string) bool {
	if value == "" {
		return false
	}
	for _, character := range value {
		if (character >= 'a' && character <= 'z') ||
			(character >= 'A' && character <= 'Z') ||
			(character >= '0' && character <= '9') || character == '-' {
			continue
		}
		return false
	}
	return true
}
