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
    status          text        NOT NULL CHECK (status IN ('created', 'captured', 'declined')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payments_idempotency_key UNIQUE (merchant_id, idempotency_key)
) WITH (fillfactor = 80);

-- Transactional outbox: written in the same statement as the status change, drained by OutboxRelay.
CREATE TABLE IF NOT EXISTS outbox (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic       text        NOT NULL,
    message_key text        NOT NULL,
    payload     text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
