import { beforeEach, describe, expect, it, vi } from "vitest";
import { REQUEST_TIMEOUT_MS, createPayment, getMerchantBalance, getPayment, getPlatformFees } from "./api";

describe("API client", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("sends the payment in kopecks with an idempotency key", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ id: "payment-1", status: "created" }), { status: 201 }));

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

    await expect(getMerchantBalance(42)).rejects.toMatchObject({
      status: 429,
      message: "Too many requests",
      retryAfterSeconds: 7,
    });
  });

  it("requests the platform fees balance from Ledger", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ balance: 123450 }), { status: 200 }));

    await expect(getPlatformFees()).resolves.toEqual({ balance: 123450 });
    // No signal from the caller, still one for the timeout.
    expect(fetch).toHaveBeenCalledWith("/balances/platform-fees", expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });

  it("parses the payment status into the known set, whatever its case", async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ id: "payment-1", status: "Captured" }));

    await expect(getPayment("payment-1")).resolves.toMatchObject({ status: "captured" });
  });

  it("fails on a status the UI does not know instead of showing it raw", async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ id: "payment-1", status: "refunded" }));

    await expect(getPayment("payment-1")).rejects.toThrow("неизвестный статус");
  });

  it("gives up on a hung API with a message, not an endless spinner", async () => {
    vi.mocked(fetch).mockRejectedValue(new DOMException("The operation timed out.", "TimeoutError"));

    await expect(getPayment("payment-1")).rejects.toThrow(`не ответил за ${REQUEST_TIMEOUT_MS / 1000} с`);
  });

  it("aborts the request when the timeout passes", async () => {
    // AbortSignal.timeout runs on the platform's own timer, which fake timers do not drive: the test fires it by hand.
    const timeout = new AbortController();
    const timeoutSpy = vi.spyOn(AbortSignal, "timeout").mockReturnValue(timeout.signal);
    let seen: AbortSignal | undefined;
    vi.mocked(fetch).mockImplementation((_url, init) => new Promise((_resolve, reject) => {
      seen = init?.signal ?? undefined;
      seen?.addEventListener("abort", () => reject(seen?.reason));
    }));

    const pending = getPayment("payment-1");
    expect(timeoutSpy).toHaveBeenCalledWith(REQUEST_TIMEOUT_MS);
    timeout.abort(new DOMException("The operation timed out.", "TimeoutError"));

    await expect(pending).rejects.toThrow("не ответил");
    expect(seen?.aborted).toBe(true);
  });

  it("keeps a cancel by the caller an AbortError, so the UI can stay silent", async () => {
    const controller = new AbortController();
    controller.abort();
    vi.mocked(fetch).mockRejectedValue(new DOMException("Aborted", "AbortError"));

    await expect(getPayment("payment-1", controller.signal)).rejects.toMatchObject({ name: "AbortError" });
  });
});
