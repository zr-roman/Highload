const localDefaults = {
  paymentsApiUrl: "",
  ledgerApiUrl: "",
};

const defaultApiUrl = import.meta.env.DEV ? null : window.location.origin;

function trimTrailingSlashes(value: string): string {
  return value.replace(/\/+$/, "");
}

export const appConfig = {
  paymentsApiUrl: trimTrailingSlashes(
    import.meta.env.VITE_PAYMENTS_API_URL || defaultApiUrl || localDefaults.paymentsApiUrl,
  ),
  ledgerApiUrl: trimTrailingSlashes(
    import.meta.env.VITE_LEDGER_API_URL || defaultApiUrl || localDefaults.ledgerApiUrl,
  ),
  apiProxyTarget: trimTrailingSlashes(import.meta.env.VITE_API_PROXY_TARGET || "http://localhost:8080"),
};
