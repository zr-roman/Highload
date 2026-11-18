-- fillfactor 80 leaves free space in every page, so the status update
-- ('created' -> 'captured') can stay on the same page as a HOT update
-- and does not touch the indexes: none of the updated columns is indexed.
CREATE TABLE IF NOT EXISTS payments (
    id              uuid        PRIMARY KEY,
    merchant_id     bigint      NOT NULL,
    idempotency_key text        NOT NULL,
    request_hash    text        NOT NULL,
    amount          bigint      NOT NULL CHECK (amount > 0),
    currency        char(3)     NOT NULL,
    fee             bigint,
    status          text        NOT NULL CHECK (status IN ('created', 'authorized', 'captured', 'declined', 'voided')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payments_idempotency_key UNIQUE (merchant_id, idempotency_key)
) WITH (fillfactor = 80);

-- Stage 4 adds 'authorized' and 'voided' (the saga); a database made before it has the CHECK of three statuses.
-- CREATE TABLE IF NOT EXISTS does not touch an existing table, so the constraint is replaced here, once: a rerun
-- finds 'voided' in it and does nothing. ALTER TABLE takes ACCESS EXCLUSIVE on payments until this transaction
-- commits, and every write of every replica waits behind it. NOT VALID makes the ADD instant: new rows are checked
-- at once, the old ones are not read. Reading them, VALIDATE CONSTRAINT, is in schema-after.sql, which DatabaseSchema
-- runs after the commit, under SHARE UPDATE EXCLUSIVE, which does not block inserts and updates.
DO $$
DECLARE
    definition text;
BEGIN
    SELECT pg_get_constraintdef(oid) INTO definition
    FROM pg_constraint
    WHERE conrelid = 'payments'::regclass AND conname = 'payments_status_check';

    IF definition IS NULL OR definition NOT LIKE '%''voided''%' THEN
        ALTER TABLE payments DROP CONSTRAINT IF EXISTS payments_status_check;
        ALTER TABLE payments ADD CONSTRAINT payments_status_check
            CHECK (status IN ('created', 'authorized', 'captured', 'declined', 'voided')) NOT VALID;
    END IF;
END $$;

-- The payments in progress, 'created' or 'authorized', for PaymentRecovery to find: a queue, like outbox. A row is
-- written by the statement that writes the payment and deleted by the one that gives it a final status (captured,
-- declined, voided), so the search reads the payments in progress, not the whole history. An index on payments for the search would not do: a partial
-- one, WHERE status = 'created', makes status an indexed column, and the status change stops being a HOT update.
-- At 1500 payments a second that cost p99 400 -> 671 ms (docs/load-testing.md, «Индекс для поиска зависших»).
--
-- Created together with its rows: the payments left in progress before the queue existed, which recovery would
-- never find otherwise. Only when the table is new, so later starts do not scan payments; schema.sql runs under an
-- advisory lock (DatabaseSchema), so two instances do not both see it new. The scan of a big payments may take
-- longer than the statement_timeout of the connection string: SET LOCAL lifts it for the transaction of the script.
SET LOCAL statement_timeout = 0;
DO $$
BEGIN
    IF to_regclass('pending_payments') IS NULL THEN
        CREATE TABLE pending_payments (
            id         uuid        PRIMARY KEY,
            created_at timestamptz NOT NULL
        );

        -- Recovery takes the oldest first.
        CREATE INDEX pending_payments_created_at ON pending_payments (created_at);

        INSERT INTO pending_payments (id, created_at)
        SELECT id, created_at FROM payments WHERE status IN ('created', 'authorized');
    END IF;
END $$;

-- Transactional outbox: written in the same statement as the status change, drained by OutboxRelay.
CREATE TABLE IF NOT EXISTS outbox (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic       text        NOT NULL,
    message_key text        NOT NULL,
    payload     text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
