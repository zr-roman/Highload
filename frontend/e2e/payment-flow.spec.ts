import { randomInt } from "node:crypto";
import { expect, test } from "@playwright/test";
import type { Payment } from "../src/api";

test("creates a payment, finds it by ID, and requests the merchant balance through the stand", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Платёжная платформа" })).toBeVisible();

  const merchantId = String(randomInt(1_000_000_000_000, 2_000_000_000_000));
  await page.locator(".create-panel").getByPlaceholder("Например, 42").fill(merchantId);
  await page.getByPlaceholder("1250,00").fill("10,00");
  await page.getByRole("button", { name: "Создать платёж" }).click();

  const createdPayment = page.locator(".create-panel .result-card");
  await expect(createdPayment).toBeVisible();
  await expect(createdPayment.locator(".mono")).toHaveText(/^[\da-f-]{36}$/i);
  const status = createdPayment.locator(".status-pill");
  await expect(status).toHaveText(/^(Проведён|Отклонён|Отменён)$/, { timeout: 60_000 });
  const finalStatus = await status.innerText();
  if (process.env.E2E_EXPECT_CAPTURED === "true") expect(finalStatus).toBe("Проведён");
  const paymentId = await createdPayment.locator(".mono").innerText();

  await page.getByPlaceholder("UUID платежа").fill(paymentId);
  const [lookup] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === `/payments/${paymentId}` && response.request().method() === "GET"),
    page.getByRole("button", { name: "Найти по ID" }).click(),
  ]);
  expect(lookup.status()).toBe(200);
  const payment: Payment = await lookup.json();
  expect(payment).toMatchObject({ id: paymentId, amount: 1000, merchantId: Number(merchantId), currency: "RUB" });
  const foundPayment = page.locator(".panel").filter({ has: page.getByRole("heading", { name: "Найти платёж" }) }).locator(".result-card");
  await expect(foundPayment).toBeVisible();
  await expect(foundPayment.locator(".mono")).toHaveText(paymentId);
  await expect(foundPayment.locator(".status-pill")).toHaveText(finalStatus);

  const balancePanel = page.locator(".balance-panel");
  await balancePanel.getByPlaceholder("Например, 42").fill(merchantId);
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
  const balance = balancePanel.locator(".balance-result").filter({ hasText: `продавец ${merchantId}` });
  await expect(balance.locator("strong")).toHaveText(netAmount);
});
