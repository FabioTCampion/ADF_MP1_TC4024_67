//go:build windows

package platform

import (
	"context"
	"errors"
	"testing"
)

func TestForcedServiceBypassesEnvironmentDetection(t *testing.T) {
	t.Parallel()
	want := errors.New("service runner called")
	detectionCalled := false

	err := run(
		true,
		func(context.Context) error { return nil },
		func() (bool, error) {
			detectionCalled = true
			return false, errors.New("detection must not run")
		},
		func(func(context.Context) error) error { return want },
		func(func(context.Context) error) error {
			return errors.New("console runner must not run")
		},
	)

	if detectionCalled {
		t.Fatal("service detection was called in forced service mode")
	}
	if !errors.Is(err, want) {
		t.Fatalf("run returned %v, want %v", err, want)
	}
}

func TestDetectedServiceUsesServiceRunner(t *testing.T) {
	t.Parallel()
	want := errors.New("service runner called")

	err := run(
		false,
		func(context.Context) error { return nil },
		func() (bool, error) { return true, nil },
		func(func(context.Context) error) error { return want },
		func(func(context.Context) error) error {
			return errors.New("console runner must not run")
		},
	)

	if !errors.Is(err, want) {
		t.Fatalf("run returned %v, want %v", err, want)
	}
}

func TestInteractiveProcessUsesConsoleRunner(t *testing.T) {
	t.Parallel()
	want := errors.New("console runner called")

	err := run(
		false,
		func(context.Context) error { return nil },
		func() (bool, error) { return false, nil },
		func(func(context.Context) error) error {
			return errors.New("service runner must not run")
		},
		func(func(context.Context) error) error { return want },
	)

	if !errors.Is(err, want) {
		t.Fatalf("run returned %v, want %v", err, want)
	}
}
