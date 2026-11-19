import { expect, it } from "vitest";
import { sleep } from "./sleep";

it("ends at once when the signal aborts, not after the pause", async () => {
  const controller = new AbortController();
  const started = performance.now();
  const pause = sleep(60_000, controller.signal);
  controller.abort();

  await expect(pause).rejects.toMatchObject({ name: "AbortError" });
  expect(performance.now() - started).toBeLessThan(1_000);
});

it("rejects at once on an already aborted signal", async () => {
  await expect(sleep(60_000, AbortSignal.abort())).rejects.toMatchObject({ name: "AbortError" });
});
