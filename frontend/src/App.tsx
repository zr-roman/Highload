import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { ApiError, createPayment, getMerchantBalance, getPayment } from "./api";
import type { MerchantBalance, Payment } from "./api";
import { appConfig } from "./config";
import "./App.css";

const money = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "RUB" });
const pendingStatuses = new Set(["created", "authorized"]);
const paymentStatuses: Record<string, string> = {
  created: "Создан",
  authorized: "Авторизован, ожидает завершения",
  captured: "Проведён",
  declined: "Отклонён",
  voided: "Отменён",
};

function minorUnitsToRubles(amount: number): string {
  return money.format(amount / 100);
}

function parseAmount(value: string): number {
  const normalized = value.trim().replace(/[\s\u00a0\u202f]/g, "").replace(",", ".");
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) {
    throw new Error("Введите сумму в рублях, не более двух знаков после запятой.");
  }

  const [rubles, fraction = ""] = normalized.split(".");
  const kopecks = BigInt(rubles) * 100n + BigInt(fraction.padEnd(2, "0"));
  if (kopecks <= 0n || kopecks > 100_000_000_000n) {
    throw new Error("Сумма должна быть больше нуля и не превышать 1 млрд рублей.");
  }
  return Number(kopecks);
}

function parseMerchantId(value: string): number {
  if (!/^\d+$/.test(value.trim())) throw new Error("ID продавца должен быть положительным целым числом.");
  const merchantId = Number(value);
  if (!Number.isSafeInteger(merchantId) || merchantId <= 0) {
    throw new Error("ID продавца должен быть положительным целым числом.");
  }
  return merchantId;
}

function describeError(error: unknown, context: "payment" | "balance" | "create" | "lookup"): string {
  if (error instanceof ApiError) {
    if (error.status === 404) {
      return context === "balance"
        ? "У продавца пока нет счёта в Ledger. После первого проведённого платежа баланс появится."
        : "Платёж с таким ID не найден.";
    }
    if (error.status === 409) return "Не удалось безопасно повторить запрос. Обновите страницу и проверьте платёж по ID.";
    if (error.status === 422) return "Этот ключ идемпотентности уже использован с другими данными. Измените запрос и отправьте его заново.";
    if (error.status === 429) {
      return error.retryAfterSeconds === null
        ? `Превышен лимит запросов. ${error.message}`
        : `Превышен лимит запросов. Повторите через ${error.retryAfterSeconds} сек.`;
    }
    if (error.status >= 500) return "Сервис временно не смог обработать запрос. Попробуйте позже.";
    return error.message;
  }

  if (error instanceof DOMException && error.name === "AbortError") return "Запрос отменён.";
  if (error instanceof Error) {
    if (context === "create") return `${error.message} Если ответ на создание платежа потерялся, повторная отправка с теми же данными использует прежний ключ защиты от дубля.`;
    return error.message;
  }
  return "Произошла неизвестная ошибка. Попробуйте ещё раз.";
}

function isFinal(payment: Payment): boolean {
  return !pendingStatuses.has(payment.status.toLowerCase());
}

function PaymentCard({ payment, message, onRefresh, refreshing }: {
  payment: Payment;
  message?: string;
  onRefresh?: () => void;
  refreshing?: boolean;
}) {
  const status = payment.status.toLowerCase();
  const positive = status === "captured";
  const negative = status === "declined" || status === "voided";

  return (
    <section className="result-card" aria-live="polite">
      <div className="result-heading">
        <div>
          <span className="section-kicker">Платёж</span>
          <h3>{minorUnitsToRubles(payment.amount)}</h3>
        </div>
        <span className={`status-pill ${positive ? "is-positive" : negative ? "is-negative" : "is-pending"}`}>
          {paymentStatuses[status] ?? payment.status}
        </span>
      </div>
      <dl className="details-grid">
        <div><dt>ID платежа</dt><dd className="mono">{payment.id}</dd></div>
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

function App() {
  const [merchantId, setMerchantId] = useState("");
  const [amount, setAmount] = useState("");
  const [createdPayment, setCreatedPayment] = useState<Payment | null>(null);
  const [createMessage, setCreateMessage] = useState("");
  const [createError, setCreateError] = useState("");
  const [creating, setCreating] = useState(false);
  const [refreshingCreated, setRefreshingCreated] = useState(false);
  const [lookupId, setLookupId] = useState("");
  const [foundPayment, setFoundPayment] = useState<Payment | null>(null);
  const [lookupError, setLookupError] = useState("");
  const [lookingUp, setLookingUp] = useState(false);
  const [balanceMerchantId, setBalanceMerchantId] = useState("");
  const [balance, setBalance] = useState<MerchantBalance | null>(null);
  const [balanceIsEmpty, setBalanceIsEmpty] = useState(false);
  const [balanceError, setBalanceError] = useState("");
  const [loadingBalance, setLoadingBalance] = useState(false);
  const pendingAttempt = useRef<{ fingerprint: string; key: string } | null>(null);
  const activeRequest = useRef<AbortController | null>(null);

  useEffect(() => () => activeRequest.current?.abort(), []);

  async function refreshCreatedPayment() {
    if (!createdPayment) return;
    setRefreshingCreated(true);
    try {
      const latest = await getPayment(createdPayment.id);
      setCreatedPayment(latest);
      setCreateMessage(isFinal(latest) ? "Статус платежа обновлён." : "Платёж ещё обрабатывается.");
    } catch (error) {
      setCreateMessage(describeError(error, "lookup"));
    } finally {
      setRefreshingCreated(false);
    }
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setCreateError("");
    setCreateMessage("");

    let parsedMerchantId: number;
    let amountInKopecks: number;

    try {
      parsedMerchantId = parseMerchantId(merchantId);
      amountInKopecks = parseAmount(amount);
    } catch (error) {
      setCreateError(error instanceof Error ? error.message : "Проверьте введённые данные.");
      return;
    }

    const fingerprint = `${parsedMerchantId}:${amountInKopecks}:RUB`;

    if (pendingAttempt.current?.fingerprint !== fingerprint)
      pendingAttempt.current = { fingerprint, key: crypto.randomUUID() };
    
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    setCreating(true);

    try {
      const payment = await createPayment(
        { merchantId: parsedMerchantId, amount: amountInKopecks, currency: "RUB" },
        pendingAttempt.current.key,
        controller.signal,
      );

      pendingAttempt.current = null;
      setCreatedPayment(payment);
      setCreateMessage(pendingStatuses.has(payment.status.toLowerCase())
        ? "Платёж принят. Проверяем его статус…"
        : "Ответ получен от Payments API.");

      if (!isFinal(payment)) {
        const deadline = Date.now() + 30_000;
        try {
          while (Date.now() < deadline && !controller.signal.aborted) {
            await new Promise<void>((resolve) => window.setTimeout(resolve, 1_000));
            const latest = await getPayment(payment.id, controller.signal);
            setCreatedPayment(latest);
            if (isFinal(latest)) {
              setCreateMessage("Платёж завершён. Итоговый статус получен из Payments API.");
              break;
            }
          }
          if (!controller.signal.aborted && Date.now() >= deadline) {
            setCreateMessage("Платёж всё ещё обрабатывается. Можно проверить его позже кнопкой «Обновить статус».");
          }
        } catch (error) {
          if (!controller.signal.aborted) {
            setCreateMessage(`Платёж создан, но статус не удалось обновить: ${describeError(error, "lookup")}`);
          }
        }
      }
    } catch (error) {
      if (error instanceof ApiError && error.status < 500) pendingAttempt.current = null;
      if (controller.signal.aborted) return;
      setCreateError(describeError(error, "create"));
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
      setCreating(false);
    }
  }

  async function handleLookup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLookupError("");
    setFoundPayment(null);
    const id = lookupId.trim();
    if (!/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(id)) {
      setLookupError("Введите ID платежа в формате UUID.");
      return;
    }

    setLookingUp(true);
    
    try {
      setFoundPayment(await getPayment(id));
    } catch (error) {
      setLookupError(describeError(error, "lookup"));
    } finally {
      setLookingUp(false);
    }
  }

  async function refreshFoundPayment() {
    if (!foundPayment) return;
    setLookingUp(true);
    setLookupError("");
    try {
      setFoundPayment(await getPayment(foundPayment.id));
    } catch (error) {
      setLookupError(describeError(error, "lookup"));
    } finally {
      setLookingUp(false);
    }
  }

  async function handleBalance(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBalanceError("");
    setBalance(null);
    setBalanceIsEmpty(false);
    let id: number;
    try {
      id = parseMerchantId(balanceMerchantId);
    } catch (error) {
      setBalanceError(error instanceof Error ? error.message : "Проверьте ID продавца.");
      return;
    }

    setLoadingBalance(true);
    try {
      setBalance(await getMerchantBalance(id));
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) setBalanceIsEmpty(true);
      else setBalanceError(describeError(error, "balance"));
    } finally {
      setLoadingBalance(false);
    }
  }

  return (
    <main className="app-shell">
      <header className="topbar">
        <a className="brand" href="#top" aria-label="Платёжная платформа, начало">
          <span className="brand-mark" aria-hidden="true">H</span>
          <span>Highload <small>PAYMENTS</small></span>
        </a>
        <span className="environment"><span />Учебный стенд</span>
      </header>

      <section className="hero" id="top">
        <p className="eyebrow">Операционная панель</p>
        <h1>Платёжная платформа</h1>
        <p className="app-description">Создавайте тестовые платежи, проверяйте их статус и смотрите баланс продавца.</p>
      </section>

      <div className="workspace">
        <section className="panel create-panel">
          <div className="panel-heading">
            <span className="panel-icon" aria-hidden="true">↗</span>
            <div><p className="section-kicker">Новая операция</p><h2>Создать платёж</h2></div>
          </div>
          <form className="form-stack" onSubmit={handleCreate}>
            <label>
              ID продавца
              <input value={merchantId} onChange={(event) => setMerchantId(event.target.value)} inputMode="numeric" placeholder="Например, 42" required />
            </label>
            <label>
              Сумма платежа
              <div className="input-suffix">
                <input value={amount} onChange={(event) => setAmount(event.target.value)} inputMode="decimal" placeholder="1250,00" required />
                <span>₽ RUB</span>
              </div>
              <small className="field-hint">Сумма будет отправлена в API в копейках.</small>
            </label>
            <button className="primary-button" type="submit" disabled={creating}>
              {creating ? <><span className="spinner" />Обрабатываем…</> : "Создать платёж"}
            </button>
          </form>
          {createError && <p className="message error-message" role="alert">{createError}</p>}
          {createdPayment && (
            <PaymentCard
              payment={createdPayment}
              message={createMessage}
              onRefresh={() => void refreshCreatedPayment()}
              refreshing={creating || refreshingCreated}
            />
          )}
        </section>

        <section className="panel">
          <div className="panel-heading">
            <span className="panel-icon secondary" aria-hidden="true">⌕</span>
            <div><p className="section-kicker">Проверка операции</p><h2>Найти платёж</h2></div>
          </div>
          <form className="form-stack" onSubmit={handleLookup}>
            <label>
              ID платежа
              <input value={lookupId} onChange={(event) => setLookupId(event.target.value)} placeholder="UUID платежа" required />
            </label>
            <button className="secondary-button" type="submit" disabled={lookingUp}>
              {lookingUp ? <><span className="spinner dark" />Ищем…</> : "Найти по ID"}
            </button>
          </form>
          {lookupError && <p className="message error-message" role="alert">{lookupError}</p>}
          {foundPayment && (
            <PaymentCard payment={foundPayment} onRefresh={() => void refreshFoundPayment()} refreshing={lookingUp} />
          )}
        </section>

        <section className="panel balance-panel">
          <div className="panel-heading">
            <span className="panel-icon balance-icon" aria-hidden="true">₽</span>
            <div><p className="section-kicker">Счёт продавца</p><h2>Баланс</h2></div>
          </div>
          <form className="balance-form" onSubmit={handleBalance}>
            <label>
              ID продавца
              <input value={balanceMerchantId} onChange={(event) => setBalanceMerchantId(event.target.value)} inputMode="numeric" placeholder="Например, 42" required />
            </label>
            <button className="secondary-button" type="submit" disabled={loadingBalance}>
              {loadingBalance ? <><span className="spinner dark" />Загружаем…</> : "Показать баланс"}
            </button>
          </form>
          {balanceError && <p className="message error-message" role="alert">{balanceError}</p>}
          {balanceIsEmpty && <p className="empty-state" role="status">У продавца пока нет счёта в Ledger.</p>}
          {balance && (
            <div className="balance-result" aria-live="polite">
              <span>Доступный баланс · продавец {balance.merchantId}</span>
              <strong>{minorUnitsToRubles(balance.balance)}</strong>
            </div>
          )}
        </section>
      </div>

      <footer className="footer">
        <span>Payments API <code>{appConfig.paymentsApiUrl || `через Vite → ${appConfig.apiProxyTarget}`}</code></span>
        <span>Ledger <code>{appConfig.ledgerApiUrl || `через Vite → ${appConfig.apiProxyTarget}`}</code></span>
      </footer>
    </main>
  );
}

export default App;
