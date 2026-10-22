// Command bank-emulator stands in for an acquiring bank: it approves or declines card payments over HTTP
// and can be told to be slow or to fail, so that Payments.Api's timeouts and retries meet a real network call.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))

	cfg, err := configFromEnv()
	if err != nil {
		logger.Error("invalid configuration", "error", err)
		os.Exit(1)
	}

	server := &http.Server{
		Addr:              cfg.Addr,
		Handler:           NewHandler(NewBank(cfg)),
		ReadHeaderTimeout: 5 * time.Second,
	}

	// Ctrl+C cancels ctx. Until then the server runs in its own goroutine.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	go func() {
		logger.Info("bank emulator is listening", "addr", cfg.Addr, "config", cfg)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			logger.Error("server failed", "error", err)
			os.Exit(1)
		}
	}()

	<-ctx.Done()

	// Graceful shutdown: stop accepting connections and let requests in flight finish, up to 10 seconds.
	logger.Info("shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := server.Shutdown(shutdownCtx); err != nil {
		logger.Error("shutdown did not finish in time", "error", err)
	}
}
