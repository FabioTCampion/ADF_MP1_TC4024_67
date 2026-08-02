//go:build windows

package platform

import (
	"context"
	"os"
	"os/signal"
)

func runConsole(execute func(context.Context) error) error {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	return execute(ctx)
}
