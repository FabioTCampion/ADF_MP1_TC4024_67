//go:build !windows

package platform

import (
	"context"
	"os"
	"os/signal"
	"syscall"
)

func Run(_ bool, execute func(context.Context) error) error {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	return execute(ctx)
}
