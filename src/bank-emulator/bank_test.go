package main

import (
	"errors"
	"regexp"
	"sync"
	"testing"
	"time"
)

var sixDigits = regexp.MustCompile(`^\d{6}$`)

func testRequest(paymentID string) AuthorizationRequest {
	return AuthorizationRequest{PaymentID: paymentID, MerchantID: 42, Amount: 150000, Currency: "RUB"}
}

func TestAuthorizeApproves(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})

	a, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	if a.PaymentID != "p-1" || a.Status != StatusApproved {
		t.Fatalf("want an approved authorization for p-1, got %+v", a)
	}
	if !sixDigits.MatchString(a.AuthorizationCode) {
		t.Errorf("an approved payment needs a six-digit authorization code, got %q", a.AuthorizationCode)
	}
	if a.DecidedAt.IsZero() {
		t.Error("DecidedAt is not set")
	}
}

func TestAuthorizeDeclines(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 100})

	a, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	if a.Status != StatusDeclined {
		t.Fatalf("DeclinePercent=100: want %q, got %q", StatusDeclined, a.Status)
	}
	if a.AuthorizationCode != "" {
		t.Errorf("a declined payment has no authorization code, got %q", a.AuthorizationCode)
	}
}

func TestAuthorizeRepeatGetsTheStoredDecision(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})
	first, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}

	b.cfg.DeclinePercent = 100 // from now on the bank declines every new payment...
	second, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("repeat: Authorize returned an error: %v", err)
	}
	if second != first {
		t.Fatalf("...but a repeat must get the stored decision: first %+v, repeat %+v", first, second)
	}
}

func TestAuthorizeConflict(t *testing.T) {
	b := NewBank(Config{})
	first, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}

	other := testRequest("p-1")
	other.Amount = 999
	if _, err := b.Authorize(other); !errors.Is(err, ErrConflict) {
		t.Fatalf("the same payment id with another amount: want ErrConflict, got %v", err)
	}

	// A rejected request must not change what the bank remembers.
	if got, ok := b.Get("p-1"); !ok || got != first {
		t.Errorf("after a conflict the stored decision must stay: want %+v, got %+v (found: %v)", first, got, ok)
	}
	if again, err := b.Authorize(testRequest("p-1")); err != nil || again != first {
		t.Errorf("after a conflict the original request must still get its decision: want %+v, got %+v (error: %v)", first, again, err)
	}
}

func TestGet(t *testing.T) {
	b := NewBank(Config{})
	if _, ok := b.Get("p-1"); ok {
		t.Fatal("Get found a decision before any Authorize")
	}

	want, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	got, ok := b.Get("p-1")
	if !ok || got != want {
		t.Fatalf("Get after Authorize: want %+v, got %+v (found: %v)", want, got, ok)
	}
}

// Run with -race as well: go test -race ./...
func TestConcurrentRequestsGetOneDecision(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 50}) // a coin toss: separate decisions would differ

	const n = 100
	results := make([]Authorization, n)
	errs := make([]error, n)
	var wg sync.WaitGroup
	for i := range n {
		wg.Add(1)
		go func() {
			defer wg.Done()
			results[i], errs[i] = b.Authorize(testRequest("p-1"))
		}()
	}
	wg.Wait()

	for i := range n {
		if errs[i] != nil {
			t.Fatalf("request %d: Authorize returned an error: %v", i, errs[i])
		}
		if results[i] != results[0] {
			t.Fatalf("request %d got %+v, request 0 got %+v: one payment id must get one decision", i, results[i], results[0])
		}
	}
}

func TestChance(t *testing.T) {
	b := NewBank(Config{})

	b.random = func() float64 { return 0.5 }
	for _, c := range []struct {
		percent int
		want    bool
	}{{0, false}, {50, false}, {51, true}, {100, true}} {
		if got := b.chance(c.percent); got != c.want {
			t.Errorf("chance(%d) when random() returns 0.5: want %v, got %v", c.percent, c.want, got)
		}
	}

	b.random = func() float64 { return 0 }
	if b.chance(0) {
		t.Error("chance(0) must never be true, even when random() returns 0")
	}
}

func TestProcessingDelay(t *testing.T) {
	b := NewBank(Config{Latency: 50 * time.Millisecond, Jitter: 20 * time.Millisecond})
	b.random = func() float64 { return 0.5 }

	if got, want := b.processingDelay(), 60*time.Millisecond; got != want {
		t.Errorf("latency 50ms, jitter 20ms, random() returns 0.5: want %v, got %v", want, got)
	}
}

func TestVoidApproved(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})
	approved, err := b.Authorize(testRequest("p-1"))
	if err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}

	voided, err := b.Void("p-1")
	if err != nil {
		t.Fatalf("Void returned an error: %v", err)
	}
	if voided.Status != StatusVoided || voided.VoidedAt == nil {
		t.Fatalf("want a voided authorization with VoidedAt, got %+v", voided)
	}
	// The void adds to the decision, it does not replace it.
	if voided.AuthorizationCode != approved.AuthorizationCode || !voided.DecidedAt.Equal(approved.DecidedAt) {
		t.Errorf("the code and DecidedAt must stay: approved %+v, voided %+v", approved, voided)
	}
}

func TestVoidRepeatGetsTheSameAnswer(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})
	if _, err := b.Authorize(testRequest("p-1")); err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	first, err := b.Void("p-1")
	if err != nil {
		t.Fatalf("Void returned an error: %v", err)
	}

	second, err := b.Void("p-1")
	if err != nil {
		t.Fatalf("repeat: Void returned an error: %v", err)
	}
	if second.Status != StatusVoided || !second.VoidedAt.Equal(*first.VoidedAt) {
		t.Errorf("a repeat must get the same void: first %+v (at %v), repeat %+v (at %v)", first, *first.VoidedAt, second, second.VoidedAt)
	}
}

func TestVoidDeclined(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 100})
	if _, err := b.Authorize(testRequest("p-1")); err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}

	if _, err := b.Void("p-1"); !errors.Is(err, ErrNotApproved) {
		t.Fatalf("void of a declined payment: want ErrNotApproved, got %v", err)
	}
	if got, _ := b.Get("p-1"); got.Status != StatusDeclined {
		t.Errorf("a refused void must not change the decision: want %q, got %q", StatusDeclined, got.Status)
	}
}

func TestVoidUnknown(t *testing.T) {
	b := NewBank(Config{})

	if _, err := b.Void("p-1"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("void of an unknown payment: want ErrNotFound, got %v", err)
	}
	if _, ok := b.Get("p-1"); ok {
		t.Error("a void of an unknown payment must not create a decision")
	}
}

func TestAfterVoidGetAndAuthorizeSeeIt(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})
	if _, err := b.Authorize(testRequest("p-1")); err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	if _, err := b.Void("p-1"); err != nil {
		t.Fatalf("Void returned an error: %v", err)
	}

	if got, _ := b.Get("p-1"); got.Status != StatusVoided {
		t.Errorf("Get after Void: want %q, got %q", StatusVoided, got.Status)
	}
	// A repeated authorization must not approve the payment again: it gets the decision as it is now.
	again, err := b.Authorize(testRequest("p-1"))
	if err != nil || again.Status != StatusVoided {
		t.Errorf("Authorize after Void: want %q, got %+v (error: %v)", StatusVoided, again, err)
	}
}

// Run with -race as well: go test -race ./...
func TestConcurrentVoidsGetOneVoid(t *testing.T) {
	b := NewBank(Config{DeclinePercent: 0})
	if _, err := b.Authorize(testRequest("p-1")); err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}

	const n = 100
	results := make([]Authorization, n)
	errs := make([]error, n)
	var wg sync.WaitGroup
	for i := range n {
		wg.Add(1)
		go func() {
			defer wg.Done()
			results[i], errs[i] = b.Void("p-1")
		}()
	}
	wg.Wait()

	for i := range n {
		if errs[i] != nil {
			t.Fatalf("void %d: Void returned an error: %v", i, errs[i])
		}
		if !results[i].VoidedAt.Equal(*results[0].VoidedAt) {
			t.Fatalf("void %d at %v, void 0 at %v: one payment must be voided once", i, results[i].VoidedAt, results[0].VoidedAt)
		}
	}
}

func TestMerchantOf(t *testing.T) {
	b := NewBank(Config{})
	if _, ok := b.MerchantOf("p-1"); ok {
		t.Fatal("MerchantOf found a merchant before any Authorize")
	}
	if _, err := b.Authorize(testRequest("p-1")); err != nil {
		t.Fatalf("Authorize returned an error: %v", err)
	}
	if got, ok := b.MerchantOf("p-1"); !ok || got != 42 {
		t.Errorf("want merchant 42, got %d (found: %v)", got, ok)
	}
}
