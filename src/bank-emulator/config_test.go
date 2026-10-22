package main

import (
	"testing"
	"time"
)

// clearBankEnv hides BANK_* variables set in the terminal, so the tests see only their own settings.
func clearBankEnv(t *testing.T) {
	for _, name := range []string{"BANK_ADDR", "BANK_LATENCY_MS", "BANK_JITTER_MS", "BANK_DECLINE_PERCENT",
		"BANK_ERROR_PERCENT", "BANK_SLOW_PERCENT", "BANK_SLOW_MS"} {
		t.Setenv(name, "")
	}
}

func TestConfigDefaults(t *testing.T) {
	clearBankEnv(t)
	cfg, err := configFromEnv()
	if err != nil {
		t.Fatalf("defaults must be valid: %v", err)
	}
	if cfg.Addr != ":5300" || cfg.Latency != 50*time.Millisecond || cfg.DeclinePercent != 2 {
		t.Errorf("unexpected defaults: %+v", cfg)
	}
}

func TestConfigFromEnv(t *testing.T) {
	clearBankEnv(t)
	t.Setenv("BANK_LATENCY_MS", "10")
	t.Setenv("BANK_SLOW_PERCENT", "5")

	cfg, err := configFromEnv()
	if err != nil {
		t.Fatalf("valid settings were rejected: %v", err)
	}
	if cfg.Latency != 10*time.Millisecond || cfg.SlowPercent != 5 {
		t.Errorf("settings were not applied: %+v", cfg)
	}
}

func TestConfigRejectsBadValues(t *testing.T) {
	for name, value := range map[string]string{
		"BANK_DECLINE_PERCENT": "150",
		"BANK_LATENCY_MS":      "fast",
	} {
		t.Run(name, func(t *testing.T) {
			clearBankEnv(t)
			t.Setenv(name, value)
			if _, err := configFromEnv(); err == nil {
				t.Errorf("%s=%s must be rejected", name, value)
			}
		})
	}
}
