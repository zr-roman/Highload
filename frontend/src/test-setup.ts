import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

afterEach(() => {
  cleanup();
  // The pending idempotency key lives there: one test's attempt must not leak into the next.
  sessionStorage.clear();
});
