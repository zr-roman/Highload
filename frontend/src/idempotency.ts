/**
 * The Idempotency-Key of a payment whose outcome is not known yet, kept in sessionStorage: a lost response followed
 * by F5 and the same payment entered again must reuse the key, or Payments API takes it for a new payment.
 * sessionStorage, not localStorage: one tab, gone with it, and two tabs do not share an attempt.
 * Storage can be missing or throw (private mode, blocked site data); then the key lives in memory only, as before.
 */
const STORAGE_KEY = "highload.pending-payment";

interface PendingAttempt {
  fingerprint: string;
  key: string;
}

let inMemory: PendingAttempt | null = null;

function read(): PendingAttempt | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return inMemory;
    const parsed: unknown = JSON.parse(raw);
    if (parsed && typeof parsed === "object" && "fingerprint" in parsed && "key" in parsed &&
        typeof parsed.fingerprint === "string" && typeof parsed.key === "string") {
      return { fingerprint: parsed.fingerprint, key: parsed.key };
    }
  } catch {
    // Unavailable or corrupted storage: fall back to memory.
  }
  return inMemory;
}

function write(attempt: PendingAttempt | null): void {
  inMemory = attempt;
  try {
    if (attempt) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(attempt));
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Memory still has it.
  }
}

/** The key for this payment: the pending one if the same payment is sent again, a new one otherwise. */
export function idempotencyKeyFor(fingerprint: string): string {
  const pending = read();
  if (pending?.fingerprint === fingerprint) return pending.key;
  const next = { fingerprint, key: crypto.randomUUID() };
  write(next);
  return next.key;
}

/** The outcome is known (a response, or a 4xx that rejects the request itself): the next payment gets a new key. */
export function settleIdempotencyKey(): void {
  write(null);
}
