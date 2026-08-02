package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"time"

	"github.com/FabioTCampion/ADF_MP1_TC4024_67/production-connector/internal/connector"
	"github.com/FabioTCampion/ADF_MP1_TC4024_67/production-connector/internal/platform"
)

var version = "development"

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run() error {
	configPath := flag.String("config", connector.DefaultConfigPath(), "absolute path to connector configuration")
	forceService := flag.Bool("service", false, "run as a Windows Service")
	testUpstream := flag.Bool("test-upstream", false, "validate the configured ERP endpoint and exit")
	apiKeyFile := flag.String("api-key-file", "", "temporary API key file used only with --test-upstream")
	validateConfig := flag.Bool("validate-config", false, "validate the connector configuration and exit")
	showVersion := flag.Bool("version", false, "print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return nil
	}
	if *validateConfig || *testUpstream {
		config, err := connector.LoadConfig(*configPath)
		if err != nil {
			return err
		}
		if *validateConfig {
			return nil
		}
		keyPath := config.APIKeyFilePath
		if *apiKeyFile != "" {
			keyPath = *apiKeyFile
		}
		ctx, cancel := context.WithTimeout(context.Background(), config.RequestTimeout()+2*time.Second)
		defer cancel()
		_, summary, err := connector.NewUpstreamClient(config).FetchUsingKeyFile(ctx, keyPath)
		if err != nil {
			return fmt.Errorf("ERP connection validation failed: %w", err)
		}
		result, err := json.Marshal(summary)
		if err != nil {
			return err
		}
		fmt.Println(string(result))
		return nil
	}

	// Enter the Windows Service dispatcher before reading the configuration.
	// This lets the process report configuration failures through the SCM instead
	// of timing out before it has connected to the service controller.
	return platform.Run(*forceService, func(ctx context.Context) error {
		config, err := connector.LoadConfig(*configPath)
		if err != nil {
			return err
		}
		logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
		gateway := connector.NewGateway(config, logger)
		return gateway.Run(ctx)
	})
}
