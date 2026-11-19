import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { ApiError, getPayment } from "../api";
import { capturedPayment as payment } from "../testing";
import { PaymentLookupPanel } from "./PaymentLookupPanel";

vi.mock("../api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api")>()),
  getPayment: vi.fn(),
}));

beforeEach(() => vi.clearAllMocks());

function lookUp(id: string) {
  fireEvent.change(screen.getByLabelText("ID платежа"), { target: { value: id } });
  fireEvent.click(screen.getByRole("button", { name: "Найти по ID" }));
}

it("looks up a payment by UUID", async () => {
  vi.mocked(getPayment).mockResolvedValue(payment);
  render(<PaymentLookupPanel />);
  lookUp(payment.id);

  expect(await screen.findByTestId("payment-status")).toHaveTextContent("Проведён");
  expect(getPayment).toHaveBeenCalledWith(payment.id, expect.any(AbortSignal));
});

it("rejects an ID that is not a UUID without calling the API", async () => {
  render(<PaymentLookupPanel />);
  lookUp("42");

  expect(await screen.findByRole("alert")).toHaveTextContent("UUID");
  expect(getPayment).not.toHaveBeenCalled();
});

it("says a payment was not found on 404", async () => {
  vi.mocked(getPayment).mockRejectedValue(new ApiError("Not Found", 404));
  render(<PaymentLookupPanel />);
  lookUp(payment.id);

  expect(await screen.findByRole("alert")).toHaveTextContent("не найден");
});

it("keeps the shown payment while its status is refreshed", async () => {
  vi.mocked(getPayment)
    .mockResolvedValueOnce({ ...payment, status: "authorized" })
    .mockResolvedValueOnce(payment);
  render(<PaymentLookupPanel />);
  lookUp(payment.id);
  expect(await screen.findByTestId("payment-status")).toHaveTextContent("Авторизован");

  fireEvent.click(screen.getByRole("button", { name: "Обновить статус" }));
  expect(screen.getByTestId("payment-card")).toBeInTheDocument();
  expect(await screen.findByText("Проведён")).toBeInTheDocument();
});
