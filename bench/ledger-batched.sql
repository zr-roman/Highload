-- Batched ledger: each pgbench client is the single consumer of one partition.
-- It owns the merchants with id % partitions = p and its own clearing (20000 + p) and fee (30000 + p)
-- sub-accounts, so no two clients ever touch the same balance row.
-- One transaction = :batch payments: all postings in one INSERT, one UPDATE per merchant in the batch.
-- Run with -c equal to partitions, e.g. -c 8 -D partitions=8 -D batch=100.
\set p :client_id % :partitions
\set per_part 10000 / :partitions
BEGIN;
WITH pay AS (
    SELECT nextval('ledger_payment_seq') AS pid,
           :p + :partitions * floor(:per_part * power(random(), 4))::bigint AS mid,
           (1000 + floor(random() * 99001))::bigint AS amount
    FROM generate_series(1, :batch)
), postings AS (
    INSERT INTO ledger_postings (payment_id, account_id, amount)
    SELECT pid, 20000 + :p, -amount FROM pay
    UNION ALL
    SELECT pid, mid, amount - amount / 50 FROM pay
    UNION ALL
    SELECT pid, 30000 + :p, amount / 50 FROM pay
), merchants AS (
    UPDATE ledger_accounts AS a
    SET balance = a.balance + d.delta
    FROM (SELECT mid, sum(amount - amount / 50) AS delta FROM pay GROUP BY mid) AS d
    WHERE a.id = d.mid
)
UPDATE ledger_accounts AS a
SET balance = a.balance + t.delta
FROM (SELECT 20000 + :p AS id, -sum(amount) AS delta FROM pay
      UNION ALL
      SELECT 30000 + :p, sum(amount / 50) FROM pay) AS t
WHERE a.id = t.id;
END;
