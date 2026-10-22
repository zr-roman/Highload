package main

import (
	"errors"
	"fmt"
	"os"
	"strconv"
	"time"
)

// Config comes from environment variables, so the same binary can play a fast and reliable bank
// or a slow and flaky one without code changes.
type Config struct {
	Addr           string        // BANK_ADDR: where to listen
	Latency        time.Duration // BANK_LATENCY_MS: how long a normal authorization takes
	Jitter         time.Duration // BANK_JITTER_MS: up to this much is added at random
	DeclinePercent int           // BANK_DECLINE_PERCENT: share of payments the bank declines
	ErrorPercent   int           // BANK_ERROR_PERCENT: requests that fail with 500 before anything is decided
	SlowPercent    int           // BANK_SLOW_PERCENT: requests that are decided and stored, but answered late
	SlowDelay      time.Duration // BANK_SLOW_MS: how late such an answer comes
}

func configFromEnv() (Config, error) {
	var errs []error
	millis := func(name string, def int) time.Duration {
		v, err := envInt(name, def, 0, 600_000)
		errs = append(errs, err)
		return time.Duration(v) * time.Millisecond
	}
	percent := func(name string, def int) int {
		v, err := envInt(name, def, 0, 100)
		errs = append(errs, err)
		return v
	}

	cfg := Config{
		Addr:           envString("BANK_ADDR", ":5300"),
		Latency:        millis("BANK_LATENCY_MS", 50),
		Jitter:         millis("BANK_JITTER_MS", 20),
		DeclinePercent: percent("BANK_DECLINE_PERCENT", 2),
		ErrorPercent:   percent("BANK_ERROR_PERCENT", 0),
		SlowPercent:    percent("BANK_SLOW_PERCENT", 0),
		SlowDelay:      millis("BANK_SLOW_MS", 5000),
	}
	// errors.Join drops nil errors and returns nil when nothing went wrong.
	return cfg, errors.Join(errs...)
}

func envString(name, def string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return def
}

func envInt(name string, def, lo, hi int) (int, error) {
	raw := os.Getenv(name)
	if raw == "" {
		return def, nil
	}
	v, err := strconv.Atoi(raw)
	if err != nil || v < lo || v > hi {
		return def, fmt.Errorf("%s=%q: want a whole number from %d to %d", name, raw, lo, hi)
	}
	return v, nil
}
