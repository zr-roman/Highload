import { expect, test } from "@playwright/test";

test("creates a payment, finds it by ID, and requests the merchant balance through the stand", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Платёжная платформа" })).toBeVisible();

  const merchantId = String(Date.now() % 2_000_000_000 + 1);
  await page.locator(".create-panel").getByPlaceholder("Например, 42").fill(merchantId);
  await page.getByPlaceholder("1250,00").fill("10,00");
  await page.getByRole("button", { name: "Создать платёж" }).click();

  const createdPayment = page.locator(".create-panel .result-card");
  await expect(createdPayment).toBeVisible();
  await expect(createdPayment.locator(".mono")).toHaveText(/^[\da-f-]{36}$/i);
  await expect(createdPayment.locator(".status-pill")).toHaveText(/Проведён|Отклонён|Отменён/,
    { timeout: 60_000 });
  const paymentId = await createdPayment.locator(".mono").innerText();

  await page.getByPlaceholder("UUID платежа").fill(paymentId);
  await page.getByRole("button", { name: "Найти по ID" }).click();
  const foundPayment = page.locator(".panel").filter({ has: page.getByRole("heading", { name: "Найти платёж" }) }).locator(".result-card");
  await expect(foundPayment).toBeVisible();
  await expect(foundPayment.locator(".mono")).toHaveText(paymentId);

  const balancePanel = page.locator(".balance-panel");
  await balancePanel.getByPlaceholder("Например, 42").fill(merchantId);
  await balancePanel.getByRole("button", { name: "Показать баланс" }).click();
  await expect(balancePanel.getByRole("status").or(balancePanel.locator(".balance-result"))).toBeVisible();
});
