package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/remote-assist/remote-assist/internal/app"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.LUTC | log.Lmicroseconds)
	cfg, err := app.LoadConfig()
	if err != nil {
		log.Fatalf("configuration: %v", err)
	}
	store, err := app.OpenStore(cfg.MySQLDSN)
	if err != nil {
		log.Fatalf("database: %v", err)
	}
	defer store.Close()

	created, err := store.EnsureBootstrapAdmin(context.Background(), cfg.TechUsername, cfg.TechPassword)
	if err != nil {
		log.Fatalf("bootstrap administrator: %v", err)
	}
	if created {
		log.Printf("bootstrap administrator %q persisted to MySQL", cfg.TechUsername)
	}

	handler := app.NewServer(cfg, store).Handler()
	httpServer := &http.Server{
		Addr:              cfg.ListenAddr,
		Handler:           handler,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       90 * time.Second,
		MaxHeaderBytes:    1 << 20,
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		<-ctx.Done()
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		_ = httpServer.Shutdown(shutdownCtx)
	}()

	log.Printf("Remote Assist listening on %s public=%s", cfg.ListenAddr, cfg.PublicBaseURL)
	if err := httpServer.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatalf("http server: %v", err)
	}
}
