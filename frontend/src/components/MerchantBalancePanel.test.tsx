import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { ApiError, getMerchantBalance, getPlatformFees } from "../api";
import { MerchantBalancePanel } from "./MerchantBalancePanel";

vi.mock("../api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api")>()),
  getMerchantBalance: vi.fn(),
  getPlatformFees: vi.fn(),
}));

beforeEach(() => vi.clearAllMocks());

function requestBalance(merchantId: string) {
  fireEvent.change(screen.getByLabelText("ID продавца"), { target: { value: merchantId } });
  fireEvent.click(screen.getByRole("button", { name: "Показать баланс" }));
}

it("shows the merchant balance returned by Ledger", async () => {
  vi.mocked(getMerchantBalance).mockResolvedValue({ merchantId: 42, balance: 250000 });
  render(<MerchantBalancePanel />);
  requestBalance("42");

  expect(await screen.findByTestId("merchant-balance")).toHaveTextContent(/продавец 42.*2\s?500,00/);
  expect(getMerchantBalance).toHaveBeenCalledWith(42, expect.any(AbortSignal));
});

it("tells an empty account from a failure", async () => {
  vi.mocked(getMerchantBalance).mockRejectedValue(new ApiError("Not Found", 404));
  render(<MerchantBalancePanel />);
  requestBalance("42");

  expect(await screen.findByRole("status")).toHaveTextContent("нет счёта");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("shows platform fees returned by Ledger in rubles", async () => {
  vi.mocked(getPlatformFees).mockResolvedValue({ balance: 123450 });
  render(<MerchantBalancePanel />);
  fireEvent.click(screen.getByRole("button", { name: "Показать комиссии" }));

  expect(await screen.findByTestId("platform-fees")).toHaveTextContent(/1\s?234,50/);
  expect(getPlatformFees).toHaveBeenCalledOnce();
});
