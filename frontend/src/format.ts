const money = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "RUB" });

/** Amounts travel in kopecks; this is the one place they become rubles for the screen. */
export function minorUnitsToRubles(amount: number): string {
  return money.format(amount / 100);
}

/** "1 250,50" -> 125050 kopecks. BigInt, so no float rounding sneaks in on the way. */
export function parseAmount(value: string): number {
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

export function parseMerchantId(value: string): number {
  if (!/^\d+$/.test(value.trim())) throw new Error("ID продавца должен быть положительным целым числом.");
  const merchantId = Number(value);
  if (!Number.isSafeInteger(merchantId) || merchantId <= 0) {
    throw new Error("ID продавца должен быть положительным целым числом.");
  }
  return merchantId;
}

const uuid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i;

export function parsePaymentId(value: string): string {
  const id = value.trim();
  if (!uuid.test(id)) throw new Error("Введите ID платежа в формате UUID.");
  return id;
}
