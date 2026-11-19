import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import LedgerDiagnostics from "./LedgerDiagnostics";

afterEach(() => { vi.unstubAllGlobals(); });

it("only scans on request and distinguishes a failed request from inconsistent postings", async () => {
  const fetchLedger = vi.fn()
    .mockResolvedValueOnce(new Response("", { status: 503 }))
    .mockResolvedValueOnce(Response.json({ ok: false, payments: 21, mismatchedAccounts: 2, unbalancedPayments: 3, totalBalance: -175 }));
  vi.stubGlobal("fetch", fetchLedger);
  render(<LedgerDiagnostics />);
  const button = screen.getByRole("button", { name: "Проверить журнал" });
  expect(fetchLedger).not.toHaveBeenCalled();
  fireEvent.click(button);
  expect(button).toBeDisabled();
  expect(await screen.findByRole("alert")).toHaveTextContent("Не удалось получить результат");
  fireEvent.click(button);
  expect(await screen.findByText("Обнаружены расхождения")).toBeVisible();
  expect(screen.getByText(/-1,75/)).toBeVisible();
  expect(fetchLedger).toHaveBeenLastCalledWith("/invariants", expect.objectContaining({ signal: expect.any(AbortSignal) }));
  expect(fetchLedger).toHaveBeenCalledTimes(2);
});
