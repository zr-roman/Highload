-- Balances. Account ids: 'merchant:<id>', 'clearing:p<partition>', 'fee:p<partition>'.
-- Clearing and fee accounts are split by Kafka partition: every partition has its own pair,
-- so no two consumers ever update the same row (experiments 4 and 5).
CREATE TABLE IF NOT EXISTS accounts (
    id      text   PRIMARY KEY,
    balance bigint NOT NULL
);

-- Append-only journal: every payment is three postings that sum to zero. Debit < 0, credit > 0.
-- No foreign key to accounts on purpose: an FK check locks the referenced (hot) account row.
CREATE TABLE IF NOT EXISTS postings (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id uuid        NOT NULL,
    account_id text        NOT NULL,
    amount     bigint      NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Deduplication: Kafka delivers at least once, a payment is applied at most once.
CREATE TABLE IF NOT EXISTS processed_payments (
    payment_id uuid PRIMARY KEY
);
