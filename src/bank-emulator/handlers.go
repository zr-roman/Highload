package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"
)

// NewHandler routes the bank's HTTP API. Method and path patterns need Go 1.22 or newer.
func NewHandler(b *Bank) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/authorizations", b.handleAuthorize)
	mux.HandleFunc("GET /v1/authorizations/{paymentId}", b.handleGet)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("ok"))
	})
	return mux
}

// handleAuthorize: POST /v1/authorizations.
func (b *Bank) handleAuthorize(w http.ResponseWriter, r *http.Request) {

	var req AuthorizationRequest
	err := decodeJSON(w, r, &req)

	if err != nil {
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}

	msg := validate(req)
	if msg != "" {
		writeError(w, http.StatusBadRequest, msg)
		return
	}

	if b.chance(b.cfg.ErrorPercent) {
		writeError(w, http.StatusInternalServerError, "simulated bank failure")
		return
	}

	if !sleep(r.Context(), b.processingDelay()) {
		return // клиент ушёл, пока банк «думал»: решения нет, отвечать некому
	}

	auth, err := b.Authorize(req)
	if errors.Is(err, ErrConflict) {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}

	if b.chance(b.cfg.SlowPercent) && !sleep(r.Context(), b.cfg.SlowDelay) {
		return // клиент не дождался, но решение уже лежит в банке
	}

	writeJSON(w, http.StatusOK, auth)
}

// handleGet: GET /v1/authorizations/{paymentId}.
func (b *Bank) handleGet(w http.ResponseWriter, r *http.Request) {
	auth, ok := b.Get(r.PathValue("paymentId"))
	if !ok {
		writeError(w, http.StatusNotFound, "there is no decision")
		return
	}

	writeJSON(w, http.StatusOK, auth)
}

// validate returns what is wrong with the request, or "" if nothing is.
func validate(req AuthorizationRequest) string {
	switch {
	case req.PaymentID == "":
		return "paymentId is required"
	case req.MerchantID <= 0:
		return "merchantId must be positive"
	case req.Amount <= 0:
		return "amount must be positive"
	case len(req.Currency) != 3:
		return "currency must be a three-letter code"
	}

	return ""
}

// sleep waits for d or until ctx is cancelled. It reports whether the whole time passed.
func sleep(ctx context.Context, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-timer.C: // время вышло: дождались
		return true
	case <-ctx.Done(): // запрос отменили раньше
		return false
	}
}

func decodeJSON(w http.ResponseWriter, r *http.Request, v any) error {
	body := http.MaxBytesReader(w, r.Body, 64<<10) // a payment is tiny; refuse anything over 64 KB
	if err := json.NewDecoder(body).Decode(v); err != nil {
		return fmt.Errorf("invalid JSON: %w", err)
	}
	return nil
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(v) // an error here means the client is gone; nothing to do about it
}

type errorResponse struct {
	Error string `json:"error"`
}

func writeError(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, errorResponse{Error: msg})
}
