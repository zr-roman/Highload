import type { Payment } from "./api";

export const capturedPayment: Payment = {
  id: "123e4567-e89b-12d3-a456-426614174000",
  merchantId: 42,
  amount: 125050,
  currency: "RUB",
  fee: null,
  status: "captured",
  createdAt: "2026-01-01T12:00:00Z",
};
