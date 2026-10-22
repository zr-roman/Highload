package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

const validBody = `{"paymentId":"p-1","merchantId":42,"amount":150000,"currency":"RUB"}`

// call sends one request straight to the handler, without a network: the response lands in a recorder.
func call(t *testing.T, h http.Handler, method, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(method, path, strings.NewReader(body)))
	return rec
}

// decode reads the JSON body of a response into a value of type T.
func decode[T any](t *testing.T, rec *httptest.ResponseRecorder) T {
	t.Helper()
	var v T
	if err := json.Unmarshal(rec.Body.Bytes(), &v); err != nil {
		t.Fatalf("response body is not JSON: %v; body: %q", err, rec.Body.String())
	}
	return v
}

func TestPostApproves(t *testing.T) {
	h := NewHandler(NewBank(Config{}))

	rec := call(t, h, http.MethodPost, "/v1/authorizations", validBody)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body)
	}
	if ct := rec.Header().Get("Content-Type"); ct != "application/json" {
		t.Errorf("want Content-Type application/json, got %q", ct)
	}
	a := decode[Authorization](t, rec)
	if a.PaymentID != "p-1" || a.Status != StatusApproved || a.AuthorizationCode == "" {
		t.Errorf("want an approved authorization with a code for p-1, got %+v", a)
	}
}

func TestPostValidation(t *testing.T) {
	h := NewHandler(NewBank(Config{}))

	cases := []struct{ name, body string }{
		{"not JSON", `{"paymentId":`},
		{"no paymentId", `{"merchantId":42,"amount":150000,"currency":"RUB"}`},
		{"no merchantId", `{"paymentId":"p-1","amount":150000,"currency":"RUB"}`},
		{"zero amount", `{"paymentId":"p-1","merchantId":42,"amount":0,"currency":"RUB"}`},
		{"negative amount", `{"paymentId":"p-1","merchantId":42,"amount":-5,"currency":"RUB"}`},
		{"bad currency", `{"paymentId":"p-1","merchantId":42,"amount":150000,"currency":"RUBLE"}`},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			rec := call(t, h, http.MethodPost, "/v1/authorizations", c.body)
			if rec.Code != http.StatusBadRequest {
				t.Fatalf("want 400, got %d: %s", rec.Code, rec.Body)
			}
			if e := decode[errorResponse](t, rec); e.Error == "" {
				t.Error(`a 400 needs a JSON body like {"error": "what is wrong"}`)
			}
		})
	}
}

func TestPostRepeatAndConflict(t *testing.T) {
	h := NewHandler(NewBank(Config{}))
	first := call(t, h, http.MethodPost, "/v1/authorizations", validBody)
	if first.Code != http.StatusOK {
		t.Fatalf("first POST: want 200, got %d: %s", first.Code, first.Body)
	}

	repeat := call(t, h, http.MethodPost, "/v1/authorizations", validBody)
	if repeat.Code != http.StatusOK {
		t.Fatalf("repeat: want 200, got %d: %s", repeat.Code, repeat.Body)
	}
	if a, b := decode[Authorization](t, first), decode[Authorization](t, repeat); a != b {
		t.Errorf("a repeat must return the stored decision: first %+v, repeat %+v", a, b)
	}

	other := strings.Replace(validBody, "150000", "999", 1)
	if rec := call(t, h, http.MethodPost, "/v1/authorizations", other); rec.Code != http.StatusConflict {
		t.Errorf("the same paymentId with another amount: want 409, got %d: %s", rec.Code, rec.Body)
	}
}

func TestGetAuthorization(t *testing.T) {
	h := NewHandler(NewBank(Config{}))
	if rec := call(t, h, http.MethodGet, "/v1/authorizations/p-1", ""); rec.Code != http.StatusNotFound {
		t.Fatalf("before any POST: want 404, got %d: %s", rec.Code, rec.Body)
	}

	posted := call(t, h, http.MethodPost, "/v1/authorizations", validBody)
	rec := call(t, h, http.MethodGet, "/v1/authorizations/p-1", "")
	if rec.Code != http.StatusOK {
		t.Fatalf("after POST: want 200, got %d: %s", rec.Code, rec.Body)
	}
	if want, got := decode[Authorization](t, posted), decode[Authorization](t, rec); want != got {
		t.Errorf("GET must return the decision made by POST: posted %+v, got %+v", want, got)
	}
}

func TestSimulatedErrorDecidesNothing(t *testing.T) {
	h := NewHandler(NewBank(Config{ErrorPercent: 100}))

	rec := call(t, h, http.MethodPost, "/v1/authorizations", validBody)
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("ErrorPercent=100: want 500, got %d: %s", rec.Code, rec.Body)
	}
	if rec := call(t, h, http.MethodGet, "/v1/authorizations/p-1", ""); rec.Code != http.StatusNotFound {
		t.Errorf("a failure before the decision must decide nothing: want 404 from GET, got %d", rec.Code)
	}
}

func TestSlowAnswerComesAfterTheDecision(t *testing.T) {
	const slow = 300 * time.Millisecond
	h := NewHandler(NewBank(Config{SlowPercent: 100, SlowDelay: slow}))

	start := time.Now()
	done := make(chan *httptest.ResponseRecorder)
	go func() { done <- call(t, h, http.MethodPost, "/v1/authorizations", validBody) }()

	// The answer is still on its way, but the decision must already be stored.
	time.Sleep(slow / 3)
	if rec := call(t, h, http.MethodGet, "/v1/authorizations/p-1", ""); rec.Code != http.StatusOK {
		t.Errorf("while the answer is late: want 200 from GET (decided first, answered later), got %d", rec.Code)
	}

	rec := <-done
	if rec.Code != http.StatusOK {
		t.Fatalf("slow answer: want 200, got %d: %s", rec.Code, rec.Body)
	}
	if elapsed := time.Since(start); elapsed < slow {
		t.Errorf("SlowPercent=100: the answer came after %v, want at least %v", elapsed, slow)
	}
}

func TestClientGivesUpBeforeTheDecision(t *testing.T) {
	h := NewHandler(NewBank(Config{Latency: 300 * time.Millisecond}))

	// The client waits 30 ms, the bank needs 300 ms: the request is cancelled during processing.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	req := httptest.NewRequest(http.MethodPost, "/v1/authorizations", strings.NewReader(validBody)).WithContext(ctx)

	start := time.Now()
	h.ServeHTTP(httptest.NewRecorder(), req)
	if elapsed := time.Since(start); elapsed > 200*time.Millisecond {
		t.Errorf("the client gave up after 30ms, but the handler kept waiting for %v: stop when the request context is done", elapsed)
	}
	if rec := call(t, h, http.MethodGet, "/v1/authorizations/p-1", ""); rec.Code != http.StatusNotFound {
		t.Errorf("a request abandoned during processing must not leave a decision: want 404 from GET, got %d", rec.Code)
	}
}

func TestHealthz(t *testing.T) {
	rec := call(t, NewHandler(NewBank(Config{})), http.MethodGet, "/healthz", "")
	if rec.Code != http.StatusOK || rec.Body.String() != "ok" {
		t.Errorf(`want 200 "ok", got %d %q`, rec.Code, rec.Body.String())
	}
}
