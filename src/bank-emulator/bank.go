package main

import (
	"errors"
	"fmt"
	"math/rand/v2"
	"sync"
	"time"
)

const (
	StatusApproved = "approved"
	StatusDeclined = "declined"
)

// ErrConflict: the payment id was already used for a payment with other details.
var ErrConflict = errors.New("payment id already used with different payment details")

// AuthorizationRequest is what Payments.Api asks the bank.
type AuthorizationRequest struct {
	PaymentID  string `json:"paymentId"`
	MerchantID int64  `json:"merchantId"`
	Amount     int64  `json:"amount"`   // in minor units: kopecks
	Currency   string `json:"currency"` // ISO 4217, for example "RUB"
}

// Authorization is the bank's decision. The same payment id always gets the same decision.
type Authorization struct {
	PaymentID         string    `json:"paymentId"`
	Status            string    `json:"status"`                      // StatusApproved or StatusDeclined
	AuthorizationCode string    `json:"authorizationCode,omitempty"` // six digits, only for approved payments
	DecidedAt         time.Time `json:"decidedAt"`
}

// decision is what the bank remembers about a payment: the request, to detect conflicts, and the answer.
type decision struct {
	request       AuthorizationRequest
	authorization Authorization
}

// Bank decides on payments and remembers every decision. It is safe for concurrent use.
type Bank struct {
	cfg Config

	// random returns a number in [0, 1). Tests replace it to make outcomes predictable.
	random func() float64

	mu        sync.Mutex // guards decisions
	decisions map[string]decision
}

func NewBank(cfg Config) *Bank {
	return &Bank{
		cfg:       cfg,
		random:    rand.Float64,
		decisions: make(map[string]decision),
	}
}

// Authorize returns the earlier decision for this payment id, or decides now and remembers it.
// A repeated request with the same payment id but other details is a client error: ErrConflict.
func (b *Bank) Authorize(req AuthorizationRequest) (Authorization, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	d, ok := b.decisions[req.PaymentID]

	// Платёж уже был: те же данные — сохранённый ответ, другие — конфликт.
	if ok {
		if req != d.request {
			return Authorization{}, ErrConflict
		}
		return d.authorization, nil
	}

	// Новый платёж: решить, запомнить, ответить.
	auth := Authorization{DecidedAt: time.Now().UTC(), Status: StatusApproved, PaymentID: req.PaymentID}
	if b.chance(b.cfg.DeclinePercent) {
		auth.Status = StatusDeclined
	} else {
		auth.AuthorizationCode = fmt.Sprintf("%06d", rand.IntN(1_000_000))
	}
	b.decisions[req.PaymentID] = decision{request: req, authorization: auth}

	return auth, nil
}

// Get returns the decision for a payment id, if the bank has made one.
func (b *Bank) Get(paymentID string) (Authorization, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()

	d, ok := b.decisions[paymentID]
	return d.authorization, ok
}

// chance reports true with the given probability in percent: 0 never, 100 always.
func (b *Bank) chance(percent int) bool {
	return b.random()*100 < float64(percent)
}

// processingDelay is how long a normal authorization takes: the latency plus a random part of the jitter.
func (b *Bank) processingDelay() time.Duration {
	return b.cfg.Latency + time.Duration(b.random()*float64(b.cfg.Jitter))
}
