//go:build windows

package platform

import (
	"context"
	"errors"

	"golang.org/x/sys/windows/svc"
)

const serviceName = "CPNTeckProductionConnector"

func Run(forceService bool, execute func(context.Context) error) error {
	isService, err := svc.IsWindowsService()
	if err != nil {
		return err
	}
	if !forceService && !isService {
		return runConsole(execute)
	}
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
