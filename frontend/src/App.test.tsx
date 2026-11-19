import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import App from "./App";

// The panels have their own tests (components/*.test.tsx); this one checks only that the page puts them together.
it("lays out the three panels as named regions", () => {
  render(<App />);

  for (const name of ["Создать платёж", "Найти платёж", "Баланс", "Комиссии"])
    expect(screen.getByRole("region", { name })).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Диагностика Ledger" })).toHaveAttribute("href", "/diagnostics");
});
