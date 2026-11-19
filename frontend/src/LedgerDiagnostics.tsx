import { useEffect, useRef, useState } from "react";
import { getLedgerInvariants, type LedgerInvariants } from "./api";

export default function LedgerDiagnostics() {
  const [result, setResult] = useState<LedgerInvariants | null>(null);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  const active = useRef<AbortController | null>(null);

  useEffect(() => () => active.current?.abort(), []);

  async function checkLedger() {
    if (active.current) return;
    const controller = new AbortController();
    active.current = controller;
    setResult(null);
    setFailed(false);
    setLoading(true);
    try {
      const snapshot = await getLedgerInvariants(controller.signal);
      if (!controller.signal.aborted) setResult(snapshot);
    } catch {
      if (!controller.signal.aborted) setFailed(true);
    } finally {
      active.current = null;
      if (!controller.signal.aborted) setLoading(false);
    }
  }

  return (
    <main className="app-shell">
      <nav className="topbar"><a href="/">← К платежам и балансам</a></nav>
      <header className="hero">
        <h1>Диагностика Ledger</h1>
        <p className="app-description">Проверка сверяет проводки и остатки по всем счетам. На большом журнале она может занять несколько минут.</p>
      </header>
      <section className="panel" aria-label="Целостность журнала">
        <button type="button" className="primary-button" disabled={loading} onClick={checkLedger}>
          {loading ? "Идёт проверка…" : "Проверить журнал"}
        </button>
        {failed && <p className="message error-message" role="alert">Не удалось получить результат из Ledger. Попробуйте ещё раз.</p>}
        <div aria-live="polite">
          {result && (
            <div className="result-card">
              <h2>{result.ok ? "Проводки и балансы сходятся" : "Обнаружены расхождения"}</h2>
              <dl className="details-grid">
                <div><dt>Обработано платежей</dt><dd>{result.payments.toLocaleString("ru-RU")}</dd></div>
                <div><dt>Платежи с несбалансированными проводками</dt><dd>{result.unbalancedPayments}</dd></div>
                <div><dt>Счета с расхождением баланса и проводок</dt><dd>{result.mismatchedAccounts}</dd></div>
                <div><dt>Сумма балансов всех счетов (ожидается 0 ₽)</dt><dd>{(result.totalBalance / 100).toLocaleString("ru-RU", { style: "currency", currency: "RUB" })}</dd></div>
              </dl>
            </div>
          )}
        </div>
      </section>
    </main>
  );
}
