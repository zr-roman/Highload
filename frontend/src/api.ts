import { appConfig } from "./config";

export interface Payment {
  id: string;
  merchantId: number;
  amount: number;
  currency: string;
  fee: number | null;
  status: string;
  createdAt: string;
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

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, init);
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new Error("Не удалось связаться с API. Проверьте, что сервис запущен и адрес настроен верно.");
  }

  const text = await response.text();
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
  });
}

export function getPayment(id: string, signal?: AbortSignal): Promise<Payment> {
  return request<Payment>(`${appConfig.paymentsApiUrl}/payments/${encodeURIComponent(id)}`, { signal });
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
