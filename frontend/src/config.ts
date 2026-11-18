const localDefaults = {
  paymentsApiUrl: "http://localhost:5100",
  ledgerApiUrl: "http://localhost:5200",
};

function trimTrailingSlashes(value: string): string {
  return value.replace(/\/+$/, "");
}

export const appConfig = {
  paymentsApiUrl: trimTrailingSlashes(
    import.meta.env.VITE_PAYMENTS_API_URL || localDefaults.paymentsApiUrl,
  ),
  ledgerApiUrl: trimTrailingSlashes(
    import.meta.env.VITE_LEDGER_API_URL || localDefaults.ledgerApiUrl,
  ),
};
