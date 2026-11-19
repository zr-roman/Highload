import { randomInt } from "node:crypto";
import { expect, test } from "@playwright/test";
import type { Payment } from "../src/api";

test("creates a payment, finds it by ID, and requests the merchant balance through the stand", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Платёжная платформа" })).toBeVisible();

  // Panels are regions named by their headings; fields by their labels; result parts by data-testid.
  // None of it depends on CSS classes, which change with the look.
  const createPanel = page.getByRole("region", { name: "Создать платёж" });
  const lookupPanel = page.getByRole("region", { name: "Найти платёж" });
  const balancePanel = page.getByRole("region", { name: "Баланс" });

  const merchantId = String(randomInt(1_000_000_000_000, 2_000_000_000_000));
  await createPanel.getByLabel("ID продавца").fill(merchantId);
  await createPanel.getByLabel("Сумма платежа").fill("10,00");
  await createPanel.getByRole("button", { name: "Создать платёж" }).click();

  const createdPayment = createPanel.getByTestId("payment-card");
  await expect(createdPayment).toBeVisible();
  await expect(createdPayment.getByTestId("payment-id")).toHaveText(/^[\da-f-]{36}$/i);
  const status = createdPayment.getByTestId("payment-status");
  await expect(status).toHaveText(/^(Проведён|Отклонён|Отменён)$/, { timeout: 60_000 });
  const finalStatus = await status.innerText();
  if (process.env.E2E_EXPECT_CAPTURED === "true") expect(finalStatus).toBe("Проведён");
  const paymentId = await createdPayment.getByTestId("payment-id").innerText();

  await lookupPanel.getByLabel("ID платежа").fill(paymentId);
  const [lookup] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === `/payments/${paymentId}` && response.request().method() === "GET"),
    lookupPanel.getByRole("button", { name: "Найти по ID" }).click(),
  ]);
  expect(lookup.status()).toBe(200);
  const payment: Payment = await lookup.json();
  expect(payment).toMatchObject({ id: paymentId, amount: 1000, merchantId: Number(merchantId), currency: "RUB" });
  const foundPayment = lookupPanel.getByTestId("payment-card");
  await expect(foundPayment).toBeVisible();
  await expect(foundPayment.getByTestId("payment-id")).toHaveText(paymentId);
  await expect(foundPayment.getByTestId("payment-status")).toHaveText(finalStatus);

  await balancePanel.getByLabel("ID продавца").fill(merchantId);
  const requestBalance = async () => {
    const [response] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === `/balances/merchants/${merchantId}` && response.request().method() === "GET"),
      balancePanel.getByRole("button", { name: "Показать баланс" }).click(),
    ]);
    return response.status();
  };
  if (finalStatus !== "Проведён") {
    expect(await requestBalance()).toBe(404);
    await expect(balancePanel.getByRole("status")).toContainText("нет счёта");
    return;
  }

  expect(payment.status).toBe("captured");
  if (payment.fee === null || !Number.isInteger(payment.fee)) throw new Error("Captured payment has no valid fee");
  const netAmount = new Intl.NumberFormat("ru-RU", { style: "currency", currency: "RUB" })
    .format((payment.amount - payment.fee) / 100);
  // Ledger creates the merchant account after consuming the capture event from Kafka.
  await expect.poll(requestBalance, { timeout: 30_000, intervals: [500, 1000, 2000] }).toBe(200);
  const balance = balancePanel.getByTestId("merchant-balance");
  await expect(balance).toContainText(`продавец ${merchantId}`);
  await expect(balance).toContainText(netAmount);
});
