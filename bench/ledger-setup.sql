-- Ledger prototype schema for pgbench experiments.
-- Accounts: merchants 0..9999, clearing sub-accounts 20000..20063, fee sub-accounts 30000..30063.
-- The naive scenario uses one clearing (20000) and one fee (30000) account;
-- in the batched scenario every partition consumer p has its own pair: 20000 + p and 30000 + p.
-- No foreign key from postings to accounts on purpose: an FK check locks the referenced (hot) account row.

DROP TABLE IF EXISTS ledger_postings;
DROP TABLE IF EXISTS ledger_accounts;
DROP SEQUENCE IF EXISTS ledger_payment_seq;

CREATE TABLE ledger_accounts (
    id      bigint PRIMARY KEY,
    kind    text   NOT NULL,
    balance bigint NOT NULL DEFAULT 0
);

-- Append-only journal. Amounts in kopecks: debit < 0, credit > 0.
CREATE TABLE ledger_postings (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id bigint NOT NULL,
    account_id bigint NOT NULL,
    amount     bigint NOT NULL
);

CREATE SEQUENCE ledger_payment_seq;

INSERT INTO ledger_accounts (id, kind) SELECT g, 'merchant' FROM generate_series(0, 9999) AS g;
INSERT INTO ledger_accounts (id, kind) SELECT 20000 + g, 'clearing' FROM generate_series(0, 63) AS g;
INSERT INTO ledger_accounts (id, kind) SELECT 30000 + g, 'fee' FROM generate_series(0, 63) AS g;

VACUUM ANALYZE ledger_accounts;
