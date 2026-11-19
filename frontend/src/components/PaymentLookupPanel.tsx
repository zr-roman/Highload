import { useId, useState, type FormEvent } from "react";
import { getPayment, type Payment } from "../api";
import { describeError } from "../errors";
import { parsePaymentId } from "../format";
import { useRequest } from "../useRequest";
import { PanelHeading } from "./PanelHeading";
import { PaymentCard } from "./PaymentCard";

export function PaymentLookupPanel() {
  const headingId = useId();
  const [lookupId, setLookupId] = useState("");
  const lookup = useRequest<Payment>();

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    let id: string;
    try {
      id = parsePaymentId(lookupId);
    } catch (error) {
      lookup.fail(error);
      return;
    }
    void lookup.run((signal) => getPayment(id, signal));
  }

  function refresh() {
    const current = lookup.data;
    if (current) void lookup.run((signal) => getPayment(current.id, signal), { keepData: true });
  }

  return (
    <section className="panel" aria-labelledby={headingId}>
      <PanelHeading id={headingId} icon="⌕" iconClassName="secondary" kicker="Проверка операции" title="Найти платёж" />
      <form className="form-stack" onSubmit={handleSubmit}>
        <label>
          ID платежа
          <input value={lookupId} onChange={(event) => setLookupId(event.target.value)} placeholder="UUID платежа" required />
        </label>
        <button className="secondary-button" type="submit" disabled={lookup.loading}>
          {lookup.loading ? <><span className="spinner dark" />Ищем…</> : "Найти по ID"}
        </button>
      </form>
      {lookup.error !== null && <p className="message error-message" role="alert">{describeError(lookup.error, "lookup")}</p>}
      {lookup.data && <PaymentCard payment={lookup.data} onRefresh={refresh} refreshing={lookup.loading} />}
    </section>
  );
}
