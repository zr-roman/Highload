import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, createPayment, getPayment } from "../api";
import { capturedPayment as payment } from "../testing";
import { CreatePaymentPanel } from "./CreatePaymentPanel";

vi.mock("../api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api")>()),
  createPayment: vi.fn(),
  getPayment: vi.fn(),
}));

function submit(merchantId = "42", amount = "1250,50") {
  fireEvent.change(screen.getByLabelText("ID продавца"), { target: { value: merchantId } });
  fireEvent.change(screen.getByLabelText("Сумма платежа"), { target: { value: amount } });
  fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));
}

const keysSent = () => vi.mocked(createPayment).mock.calls.map((call) => call[1]);

describe("creating a payment", () => {
  beforeEach(() => vi.clearAllMocks());

  it("names the amount field by its label alone and describes it with the hint", () => {
    render(<CreatePaymentPanel />);
    const amount = screen.getByRole("textbox", { name: "Сумма платежа" });
    expect(amount).toHaveAccessibleDescription(/в копейках/);
  });

  it("converts the entered amount to kopecks and displays the final payment", async () => {
    vi.mocked(createPayment).mockResolvedValue(payment);
    render(<CreatePaymentPanel />);
    submit();

    expect(await screen.findByTestId("payment-status")).toHaveTextContent("Проведён");
    expect(createPayment).toHaveBeenCalledWith({ merchantId: 42, amount: 125050, currency: "RUB" }, expect.any(String), expect.any(AbortSignal));
    expect(screen.getByTestId("payment-id")).toHaveTextContent(payment.id);
  });

  it("shows validation errors without calling the API", async () => {
    render(<CreatePaymentPanel />);
    submit("42", "0");

    expect(await screen.findByRole("alert")).toHaveTextContent("Сумма должна быть больше нуля");
    expect(createPayment).not.toHaveBeenCalled();
  });

  it("polls a pending payment until Payments API returns a final status", async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(createPayment).mockResolvedValue({ ...payment, status: "created" });
      vi.mocked(getPayment).mockResolvedValue(payment);
      render(<CreatePaymentPanel />);
      submit();

      await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
      expect(screen.getByTestId("payment-status")).toHaveTextContent("Проведён");
      expect(getPayment).toHaveBeenCalledWith(payment.id, expect.any(AbortSignal));
    } finally {
      vi.useRealTimers();
    }
  });

  it("stops polling at once when the panel goes away, without waiting out the pause", async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(createPayment).mockResolvedValue({ ...payment, status: "created" });
      const { unmount } = render(<CreatePaymentPanel />);
      submit();
      await act(async () => { await vi.advanceTimersByTimeAsync(0); });

      unmount();
      // The pause was aborted: no timer is left to fire, and no status is asked for afterwards.
      expect(vi.getTimerCount()).toBe(0);
      await vi.advanceTimersByTimeAsync(5_000);
      expect(getPayment).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("reuses the idempotency key after a network error for the same payment", async () => {
    vi.mocked(createPayment)
      .mockRejectedValueOnce(new Error("Сеть недоступна"))
      .mockResolvedValueOnce(payment);
    render(<CreatePaymentPanel />);
    submit();
    await screen.findByRole("alert");
    submit();

    await screen.findByTestId("payment-card");
    expect(keysSent()[1]).toBe(keysSent()[0]);
  });

  it("reuses the idempotency key after the page is reloaded", async () => {
    vi.mocked(createPayment)
      .mockRejectedValueOnce(new Error("Сеть недоступна"))
      .mockResolvedValueOnce(payment);
    const { unmount } = render(<CreatePaymentPanel />);
    submit();
    await screen.findByRole("alert");

    // F5: the component state is gone, sessionStorage is not.
    unmount();
    render(<CreatePaymentPanel />);
    submit();

    await screen.findByTestId("payment-card");
    expect(keysSent()[1]).toBe(keysSent()[0]);
  });

  it("takes a new key once the outcome is known", async () => {
    vi.mocked(createPayment)
      .mockRejectedValueOnce(new ApiError("Bad request", 400))
      .mockResolvedValueOnce(payment)
      .mockResolvedValueOnce(payment);
    render(<CreatePaymentPanel />);
    submit();
    await screen.findByRole("alert");
    submit();
    await screen.findByTestId("payment-card");
    submit();
    await vi.waitFor(() => expect(createPayment).toHaveBeenCalledTimes(3));

    const [afterRejection, afterSuccess, next] = keysSent();
    expect(afterSuccess).not.toBe(afterRejection);
    expect(next).not.toBe(afterSuccess);
  });
});
