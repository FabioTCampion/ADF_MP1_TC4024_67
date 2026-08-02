//go:build windows

package platform

import (
	"context"
	"errors"

	"golang.org/x/sys/windows/svc"
)

const serviceName = "CPNTeckProductionConnector"

func Run(forceService bool, execute func(context.Context) error) error {
	return run(forceService, execute, svc.IsWindowsService, runService, runConsole)
}

func run(
	forceService bool,
	execute func(context.Context) error,
	detectService func() (bool, error),
	serviceRunner func(func(context.Context) error) error,
	consoleRunner func(func(context.Context) error) error,
) error {
	// When the installer explicitly passes --service, connect to the Service
	// Control Manager immediately. Older Windows 10 LTSC builds can take long
	// enough in IsWindowsService to exceed the SCM startup timeout.
	if forceService {
		return serviceRunner(execute)
	}

	isService, err := detectService()
	if err != nil {
		return err
	}
	if !isService {
		return consoleRunner(execute)
	}
	return serviceRunner(execute)
}

func runService(execute func(context.Context) error) error {
	handler := &serviceHandler{execute: execute}
	if err := svc.Run(serviceName, handler); err != nil {
		return err
	}
	return handler.result
}

type serviceHandler struct {
	execute func(context.Context) error
	result  error
}

func (h *serviceHandler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	const accepted = svc.AcceptStop | svc.AcceptShutdown
	status <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(context.Background())
	results := make(chan error, 1)
	go func() { results <- h.execute(ctx) }()
	status <- svc.Status{State: svc.Running, Accepts: accepted}

	for {
		select {
		case request := <-requests:
			switch request.Cmd {
			case svc.Interrogate:
				status <- request.CurrentStatus
			case svc.Stop, svc.Shutdown:
				status <- svc.Status{State: svc.StopPending}
				cancel()
				h.result = <-results
				status <- svc.Status{State: svc.Stopped}
				return false, exitCode(h.result)
			}
		case h.result = <-results:
			cancel()
			status <- svc.Status{State: svc.Stopped}
			return false, exitCode(h.result)
		}
	}
}

func exitCode(err error) uint32 {
	if err == nil || errors.Is(err, context.Canceled) {
		return 0
	}
	return 1
}
