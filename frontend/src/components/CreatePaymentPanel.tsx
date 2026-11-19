import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { ApiError, createPayment, getPayment, isPending, type Payment } from "../api";
import { describeError } from "../errors";
import { parseAmount, parseMerchantId } from "../format";
import { idempotencyKeyFor, settleIdempotencyKey } from "../idempotency";
import { sleep } from "../sleep";
import { PanelHeading } from "./PanelHeading";
import { PaymentCard } from "./PaymentCard";

/** Payments API answers 202 while the saga decides; the panel asks for the outcome for this long. */
const POLL_INTERVAL_MS = 1_000;
const POLL_DEADLINE_MS = 30_000;

export function CreatePaymentPanel() {
  const headingId = useId();
  const amountId = useId();
  const amountHintId = useId();
  const [merchantId, setMerchantId] = useState("");
  const [amount, setAmount] = useState("");
  const [payment, setPayment] = useState<Payment | null>(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [creating, setCreating] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const active = useRef<AbortController | null>(null);

  useEffect(() => () => active.current?.abort(), []);

  async function pollUntilFinal(created: Payment, signal: AbortSignal) {
    const deadline = Date.now() + POLL_DEADLINE_MS;
    try {
      while (Date.now() < deadline) {
        await sleep(POLL_INTERVAL_MS, signal);
        const latest = await getPayment(created.id, signal);
        setPayment(latest);
        if (!isPending(latest.status)) {
          setMessage("Платёж завершён. Итоговый статус получен из Payments API.");
          return;
        }
      }
      setMessage("Платёж всё ещё обрабатывается. Можно проверить его позже кнопкой «Обновить статус».");
    } catch (pollError) {
      if (!signal.aborted) setMessage(`Платёж создан, но статус не удалось обновить: ${describeError(pollError, "lookup")}`);
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setMessage("");

    let parsedMerchantId: number;
    let amountInKopecks: number;
    try {
      parsedMerchantId = parseMerchantId(merchantId);
      amountInKopecks = parseAmount(amount);
    } catch (validationError) {
      setError(validationError instanceof Error ? validationError.message : "Проверьте введённые данные.");
      return;
    }

    // The same payment sent again — after a lost response, even after F5 — goes with the same key.
    const key = idempotencyKeyFor(`${parsedMerchantId}:${amountInKopecks}:RUB`);

    active.current?.abort();
    const controller = new AbortController();
    active.current = controller;
    setCreating(true);

    try {
      const created = await createPayment(
        { merchantId: parsedMerchantId, amount: amountInKopecks, currency: "RUB" },
        key,
        controller.signal,
      );
      settleIdempotencyKey();
      setPayment(created);

      if (isPending(created.status)) {
        setMessage("Платёж принят. Проверяем его статус…");
        await pollUntilFinal(created, controller.signal);
      } else {
        setMessage("Ответ получен от Payments API.");
      }
    } catch (createError) {
      // A 4xx rejects the request itself: nothing was created, and the next try is a new payment. A 5xx, a timeout
      // or a network error is ambiguous: the key stays for the retry.
      if (createError instanceof ApiError && createError.status < 500) settleIdempotencyKey();
      if (!controller.signal.aborted) setError(describeError(createError, "create"));
    } finally {
      if (active.current === controller) active.current = null;
      if (!controller.signal.aborted) setCreating(false);
    }
  }

  async function refresh() {
    if (!payment) return;
    setRefreshing(true);
    try {
      const latest = await getPayment(payment.id);
      setPayment(latest);
      setMessage(isPending(latest.status) ? "Платёж ещё обрабатывается." : "Статус платежа обновлён.");
    } catch (refreshError) {
      setMessage(describeError(refreshError, "lookup"));
    } finally {
      setRefreshing(false);
    }
  }

  return (
    <section className="panel create-panel" aria-labelledby={headingId}>
      <PanelHeading id={headingId} icon="↗" kicker="Новая операция" title="Создать платёж" />
      <form className="form-stack" onSubmit={handleSubmit}>
        <label>
          ID продавца
          <input value={merchantId} onChange={(event) => setMerchantId(event.target.value)} inputMode="numeric" placeholder="Например, 42" required />
        </label>
        {/* The label names the field with its text alone: the currency suffix and the hint stay out of the name,
            and the hint reaches a screen reader as the field's description. */}
        <div className="field">
          <label htmlFor={amountId}>Сумма платежа</label>
          <div className="input-suffix">
            <input id={amountId} aria-describedby={amountHintId} value={amount} onChange={(event) => setAmount(event.target.value)}
              inputMode="decimal" placeholder="1250,00" required />
            <span aria-hidden="true">₽ RUB</span>
          </div>
          <small id={amountHintId} className="field-hint">В рублях; в API сумма уходит в копейках.</small>
        </div>
        <button className="primary-button" type="submit" disabled={creating}>
          {creating ? <><span className="spinner" />Обрабатываем…</> : "Создать платёж"}
        </button>
      </form>
      {error && <p className="message error-message" role="alert">{error}</p>}
      {payment && (
        <PaymentCard payment={payment} message={message} onRefresh={() => void refresh()} refreshing={creating || refreshing} />
      )}
    </section>
  );
}
