import { isPending, type Payment, type PaymentStatus } from "../api";
import { minorUnitsToRubles } from "../format";

const statusLabels: Record<PaymentStatus, string> = {
  created: "Создан",
  authorized: "Авторизован, ожидает завершения",
  captured: "Проведён",
  declined: "Отклонён",
  voided: "Отменён",
};

const statusTone: Record<PaymentStatus, string> = {
  created: "is-pending",
  authorized: "is-pending",
  captured: "is-positive",
  declined: "is-negative",
  voided: "is-negative",
};

interface PaymentCardProps {
  payment: Payment;
  message?: string;
  onRefresh?: () => void;
  refreshing?: boolean;
}

export function PaymentCard({ payment, message, onRefresh, refreshing }: PaymentCardProps) {
  return (
    <section className="result-card" aria-live="polite" aria-label="Платёж" data-testid="payment-card"
      aria-busy={isPending(payment.status) || refreshing ? true : undefined}>
      <div className="result-heading">
        <div>
          <span className="section-kicker">Платёж</span>
          <h3>{minorUnitsToRubles(payment.amount)}</h3>
        </div>
        <span className={`status-pill ${statusTone[payment.status]}`} data-testid="payment-status">
          {statusLabels[payment.status]}
        </span>
      </div>
      <dl className="details-grid">
        <div><dt>ID платежа</dt><dd className="mono" data-testid="payment-id">{payment.id}</dd></div>
        <div><dt>ID продавца</dt><dd>{payment.merchantId}</dd></div>
        <div><dt>Комиссия</dt><dd>{payment.fee === null ? "Пока не рассчитана" : minorUnitsToRubles(payment.fee)}</dd></div>
        <div><dt>Создан</dt><dd>{new Date(payment.createdAt).toLocaleString("ru-RU")}</dd></div>
      </dl>
      {message && <p className="inline-note">{message}</p>}
      {onRefresh && (
        <button className="text-button" type="button" onClick={onRefresh} disabled={refreshing}>
          {refreshing ? "Проверяем…" : "Обновить статус"}
        </button>
      )}
    </section>
  );
}
