import { expect, it, vi } from "vitest";
import { idempotencyKeyFor, settleIdempotencyKey } from "./idempotency";

it("gives the same payment the same key until its outcome is known", () => {
  const first = idempotencyKeyFor("42:125050:RUB");
  expect(idempotencyKeyFor("42:125050:RUB")).toBe(first);
  expect(idempotencyKeyFor("42:999:RUB")).not.toBe(first);

  settleIdempotencyKey();
  const next = idempotencyKeyFor("42:999:RUB");
  expect(next).not.toBe(first);
});

it("keeps the key across a reload of the page", async () => {
  const before = idempotencyKeyFor("42:125050:RUB");

  // A fresh copy of the module, as after F5: its memory is empty, sessionStorage is not.
  vi.resetModules();
  const reloaded = await import("./idempotency");
  expect(reloaded.idempotencyKeyFor("42:125050:RUB")).toBe(before);
});

it("works on memory when storage is unavailable", () => {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });

  const key = idempotencyKeyFor("7:100:RUB");
  expect(idempotencyKeyFor("7:100:RUB")).toBe(key);
});
