import { useId, useState, type FormEvent } from "react";
import { ApiError, getMerchantBalance, type MerchantBalance } from "../api";
import { describeError } from "../errors";
import { minorUnitsToRubles, parseMerchantId } from "../format";
import { useRequest } from "../useRequest";
import { PanelHeading } from "./PanelHeading";
import { PlatformFeesSection } from "./PlatformFeesSection";

export function MerchantBalancePanel() {
  const headingId = useId();
  const [merchantId, setMerchantId] = useState("");
  const balance = useRequest<MerchantBalance>();

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    let id: number;
    try {
      id = parseMerchantId(merchantId);
    } catch (error) {
      balance.fail(error);
      return;
    }
    void balance.run((signal) => getMerchantBalance(id, signal));
  }

  // Ledger opens the account with the first captured payment: 404 is "nothing yet", not a failure.
  const noAccount = balance.error instanceof ApiError && balance.error.status === 404;

  return (
    <section className="panel balance-panel" aria-labelledby={headingId}>
      <PanelHeading id={headingId} icon="₽" iconClassName="balance-icon" kicker="Счёт продавца" title="Баланс" />
      <form className="balance-form" onSubmit={handleSubmit}>
        <label>
          ID продавца
          <input value={merchantId} onChange={(event) => setMerchantId(event.target.value)} inputMode="numeric" placeholder="Например, 42" required />
        </label>
        <button className="secondary-button" type="submit" disabled={balance.loading}>
          {balance.loading ? <><span className="spinner dark" />Загружаем…</> : "Показать баланс"}
        </button>
      </form>
      {balance.error !== null && !noAccount && (
        <p className="message error-message" role="alert">{describeError(balance.error, "balance")}</p>
      )}
      {noAccount && <p className="empty-state" role="status">У продавца пока нет счёта в Ledger.</p>}
      {balance.data && (
        <div className="balance-result" aria-live="polite" data-testid="merchant-balance">
          <span>Доступный баланс · продавец {balance.data.merchantId}</span>
          <strong>{minorUnitsToRubles(balance.data.balance)}</strong>
        </div>
      )}
      <PlatformFeesSection />
    </section>
  );
}
