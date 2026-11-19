import { useId } from "react";
import { getPlatformFees, type PlatformFees } from "../api";
import { describeError } from "../errors";
import { minorUnitsToRubles } from "../format";
import { useRequest } from "../useRequest";
import { PanelHeading } from "./PanelHeading";

export function PlatformFeesSection() {
  const headingId = useId();
  const fees = useRequest<PlatformFees>();

  return (
    <section className="fees-section" aria-labelledby={headingId}>
      <PanelHeading id={headingId} icon="%" iconClassName="fees-icon" kicker="Счёт платформы" title="Комиссии" />
      <p className="field-hint">Сумма комиссий, зачисленных на платформу.</p>
      <button className="secondary-button" type="button" onClick={() => void fees.run(getPlatformFees)} disabled={fees.loading}>
        {fees.loading ? <><span className="spinner dark" />Загружаем…</> : "Показать комиссии"}
      </button>
      {fees.error !== null && <p className="message error-message" role="alert">{describeError(fees.error, "platformFees")}</p>}
      {fees.data && (
        <div className="balance-result fees-result" aria-live="polite" data-testid="platform-fees">
          <span>Всего начислено комиссий</span>
          <strong>{minorUnitsToRubles(fees.data.balance)}</strong>
        </div>
      )}
    </section>
  );
}
