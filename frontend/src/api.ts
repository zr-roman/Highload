import { appConfig } from "./config";

/** Statuses of Payments API (PaymentStatus in Payments.Api). */
export const paymentStatuses = ["created", "authorized", "captured", "declined", "voided"] as const;
export type PaymentStatus = (typeof paymentStatuses)[number];

/** Not final yet: the saga is still deciding. */
export function isPending(status: PaymentStatus): boolean {
  return status === "created" || status === "authorized";
}

export interface Payment {
  id: string;
  merchantId: number;
  amount: number;
  currency: string;
  fee: number | null;
  status: PaymentStatus;
  createdAt: string;
}

/** The one place a status from the wire becomes a PaymentStatus: an unknown one fails here, not in the UI. */
export function parsePaymentStatus(value: unknown): PaymentStatus {
  const status = typeof value === "string" ? value.toLowerCase() : "";
  if ((paymentStatuses as readonly string[]).includes(status)) return status as PaymentStatus;
  throw new Error(`Payments API вернул неизвестный статус платежа: ${String(value)}.`);
}

function toPayment(raw: Payment): Payment {
  return { ...raw, status: parsePaymentStatus(raw.status) };
}

export interface MerchantBalance {
  merchantId: number;
  balance: number;
}

export interface PlatformFees {
  balance: number;
}

export class ApiError extends Error {
  readonly status: number;
  readonly retryAfterSeconds: number | null;

  constructor(
    message: string,
    status: number,
    retryAfterSeconds: number | null = null,
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

function retryAfterSeconds(value: string | null): number | null {
  if (!value) return null;
  const seconds = Number(value);
  if (Number.isFinite(seconds)) return Math.max(0, seconds);
  const date = Date.parse(value);
  return Number.isNaN(date) ? null : Math.max(0, Math.ceil((date - Date.now()) / 1000));
}

/** How long a request may take before the UI gives up on it; the button must not spin forever on a hung API. */
export const REQUEST_TIMEOUT_MS = 15_000;

interface RequestOptions extends RequestInit {
  /** Overrides REQUEST_TIMEOUT_MS, for a request that is slow by design (the Ledger check). */
  timeoutMs?: number;
}

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

function isTimeout(error: unknown): boolean {
  return error instanceof DOMException && error.name === "TimeoutError";
}

async function request<T>(url: string, { timeoutMs = REQUEST_TIMEOUT_MS, signal, ...init }: RequestOptions = {}): Promise<T> {
  // The caller's signal (a new submit, leaving the page) and the timeout, whichever comes first. The timeout also
  // covers reading the body: a server that sends the headers and then hangs is caught as well.
  const timeout = AbortSignal.timeout(timeoutMs);
  const combined = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const timeoutError = () => new Error(`API не ответил за ${Math.round(timeoutMs / 1000)} с. Попробуйте ещё раз.`);

  let response: Response;
  let text: string;
  try {
    response = await fetch(url, { ...init, signal: combined });
    text = await response.text();
  } catch (error) {
    // A cancel by the caller stays an AbortError, so the UI can tell it from a failure and say nothing.
    if (signal?.aborted && isAbort(error)) throw error;
    if (timeout.aborted || isTimeout(error)) throw timeoutError();
    if (isAbort(error)) throw error;
    throw new Error("Не удалось связаться с API. Проверьте, что сервис запущен и адрес настроен верно.");
  }

  let body: unknown;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }

  if (!response.ok) {
    const problem = body && typeof body === "object" ? (body as Record<string, unknown>) : {};
    const validationErrors = problem.errors && typeof problem.errors === "object"
      ? Object.values(problem.errors as Record<string, unknown[]>).flat().join(" ")
      : "";
    const message =
      (typeof problem.detail === "string" && problem.detail) ||
      validationErrors ||
      (typeof problem.title === "string" && problem.title) ||
      (typeof body === "string" && body) ||
      `API вернул ошибку ${response.status}.`;
    throw new ApiError(message, response.status, retryAfterSeconds(response.headers.get("Retry-After")));
  }

  return body as T;
}

export function createPayment(
  payload: { merchantId: number; amount: number; currency: "RUB" },
  idempotencyKey: string,
  signal?: AbortSignal,
): Promise<Payment> {
  return request<Payment>(`${appConfig.paymentsApiUrl}/payments`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey,
    },
    body: JSON.stringify(payload),
    signal,
  }).then(toPayment);
}

export function getPayment(id: string, signal?: AbortSignal): Promise<Payment> {
  return request<Payment>(`${appConfig.paymentsApiUrl}/payments/${encodeURIComponent(id)}`, { signal }).then(toPayment);
}

export function getMerchantBalance(merchantId: number, signal?: AbortSignal): Promise<MerchantBalance> {
  return request<MerchantBalance>(
    `${appConfig.ledgerApiUrl}/balances/merchants/${encodeURIComponent(merchantId)}`,
    { signal },
  );
}

export function getPlatformFees(signal?: AbortSignal): Promise<PlatformFees> {
  return request<PlatformFees>(`${appConfig.ledgerApiUrl}/balances/platform-fees`, { signal });
}

export interface LedgerInvariants {
  ok: boolean;
  payments: number;
  unbalancedPayments: number;
  mismatchedAccounts: number;
  totalBalance: number;
}

// The check reads the whole journal and may take minutes on a big one (LedgerDiagnostics says so).
const INVARIANTS_TIMEOUT_MS = 10 * 60_000;

export async function getLedgerInvariants(signal: AbortSignal): Promise<LedgerInvariants> {
  const snapshot = await request<LedgerInvariants>(`${appConfig.ledgerApiUrl}/invariants`, {
    signal,
    cache: "no-store",
    timeoutMs: INVARIANTS_TIMEOUT_MS,
  });
  if (typeof snapshot?.ok !== "boolean" ||
      ![snapshot.payments, snapshot.unbalancedPayments, snapshot.mismatchedAccounts, snapshot.totalBalance].every(Number.isSafeInteger)) {
    throw new Error("Ledger вернул некорректный результат проверки.");
  }
  return snapshot;
}
