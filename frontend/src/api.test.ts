import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, createPayment, getMerchantBalance } from "./api";

describe("API client", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("sends the payment in kopecks with an idempotency key", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ id: "payment-1" }), { status: 201 }));

    await createPayment({ merchantId: 42, amount: 125050, currency: "RUB" }, "key-1");

    expect(fetch).toHaveBeenCalledWith("/payments", expect.objectContaining({
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": "key-1" },
      body: JSON.stringify({ merchantId: 42, amount: 125050, currency: "RUB" }),
    }));
  });

  it("preserves API validation errors and Retry-After", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ detail: "Too many requests" }), {
      status: 429,
      headers: { "Retry-After": "7" },
    }));

    await expect(getMerchantBalance(42)).rejects.toMatchObject<ApiError>({
      status: 429,
      message: "Too many requests",
      retryAfterSeconds: 7,
    });
  });
});
