import { ApiError } from "./api";

export type ErrorContext = "create" | "lookup" | "balance" | "platformFees";

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

/** An error of any request, as a sentence for the user. */
export function describeError(error: unknown, context: ErrorContext): string {
  if (error instanceof ApiError) {
    if (error.status === 404) {
      return context === "balance"
        ? "У продавца пока нет счёта в Ledger. После первого проведённого платежа баланс появится."
        : context === "platformFees"
          ? "Данные о комиссиях платформы пока недоступны."
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

  if (isAbortError(error)) return "Запрос отменён.";
  if (error instanceof Error) {
    if (context === "create") {
      return `${error.message} Если ответ на создание платежа потерялся, повторная отправка с теми же данными использует прежний ключ защиты от дубля — даже после перезагрузки страницы.`;
    }
    return error.message;
  }
  return "Произошла неизвестная ошибка. Попробуйте ещё раз.";
}
