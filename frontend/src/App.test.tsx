import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { createPayment, getMerchantBalance, getPayment } from "./api";
import type { Payment } from "./api";

vi.mock("./api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    retryAfterSeconds: number | null;

    constructor(message: string, status: number, retryAfterSeconds: number | null = null) {
      super(message);
      this.status = status;
      this.retryAfterSeconds = retryAfterSeconds;
    }
  },
  createPayment: vi.fn(),
  getMerchantBalance: vi.fn(),
  getPayment: vi.fn(),
}));

const payment: Payment = {
  id: "123e4567-e89b-12d3-a456-426614174000",
  merchantId: 42,
  amount: 125050,
  currency: "RUB",
  fee: null,
  status: "captured",
  createdAt: "2026-01-01T12:00:00Z",
};

describe("payment dashboard", () => {
  beforeEach(() => vi.clearAllMocks());

  it("converts the entered amount to kopecks and displays the final payment", async () => {
    vi.mocked(createPayment).mockResolvedValue(payment);
    render(<App />);

    fireEvent.change(screen.getAllByLabelText("ID продавца")[0], { target: { value: "42" } });
    fireEvent.change(screen.getByPlaceholderText("1250,00"), { target: { value: "1250,50" } });
    fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));

    await screen.findByText("Проведён");
    expect(createPayment).toHaveBeenCalledWith({ merchantId: 42, amount: 125050, currency: "RUB" }, expect.any(String), expect.any(AbortSignal));
    expect(screen.getByText(payment.id)).toBeInTheDocument();
  });

  it("shows validation errors without calling the API", async () => {
    render(<App />);
    fireEvent.change(screen.getAllByLabelText("ID продавца")[0], { target: { value: "42" } });
    fireEvent.change(screen.getByPlaceholderText("1250,00"), { target: { value: "0" } });
    fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Сумма должна быть больше нуля");
    expect(createPayment).not.toHaveBeenCalled();
  });

  it("polls a pending payment until Payments API returns a final status", async () => {
    vi.useFakeTimers();
    vi.mocked(createPayment).mockResolvedValue({ ...payment, status: "created" });
    vi.mocked(getPayment).mockResolvedValue(payment);
    render(<App />);
    fireEvent.change(screen.getAllByLabelText("ID продавца")[0], { target: { value: "42" } });
    fireEvent.change(screen.getByPlaceholderText("1250,00"), { target: { value: "1250,50" } });
    fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));

    await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
    expect(screen.getByText("Проведён")).toBeInTheDocument();
    expect(getPayment).toHaveBeenCalledWith(payment.id, expect.any(AbortSignal));
    vi.useRealTimers();
  });

  it("reuses the idempotency key after a network error for the same payment", async () => {
    vi.mocked(createPayment)
      .mockRejectedValueOnce(new Error("Сеть недоступна"))
      .mockResolvedValueOnce(payment);
    render(<App />);
    fireEvent.change(screen.getAllByLabelText("ID продавца")[0], { target: { value: "42" } });
    fireEvent.change(screen.getByPlaceholderText("1250,00"), { target: { value: "1250,50" } });
    fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));
    await screen.findByRole("alert");
    fireEvent.click(screen.getByRole("button", { name: "Создать платёж" }));

    await screen.findByText("Проведён");
    expect(vi.mocked(createPayment).mock.calls[1][1]).toBe(vi.mocked(createPayment).mock.calls[0][1]);
  });

  it("looks up a payment by UUID", async () => {
    vi.mocked(getPayment).mockResolvedValue(payment);
    render(<App />);
    fireEvent.change(screen.getByPlaceholderText("UUID платежа"), { target: { value: payment.id } });
    fireEvent.click(screen.getByRole("button", { name: "Найти по ID" }));

    await screen.findByText("Проведён");
    expect(getPayment).toHaveBeenCalledWith(payment.id);
  });

  it("shows the merchant balance returned by Ledger", async () => {
    vi.mocked(getMerchantBalance).mockResolvedValue({ merchantId: 42, balance: 250000 });
    render(<App />);
    const inputs = screen.getAllByLabelText("ID продавца");
    fireEvent.change(inputs[1], { target: { value: "42" } });
    fireEvent.click(screen.getByRole("button", { name: "Показать баланс" }));

    await waitFor(() => expect(screen.getByText(/Доступный баланс/)).toBeInTheDocument());
    expect(getMerchantBalance).toHaveBeenCalledWith(42);
  });
});
