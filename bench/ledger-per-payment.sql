-- Naive ledger: one payment per transaction, every payment updates three balances.
-- Accounts: merchant :mid, shared clearing 20000 and shared fee 30000 (the hot rows, updated last).
-- Run with -D partitions=N: it only shapes the merchant distribution, the same way as in ledger-batched.sql.
\set p random(0, :partitions - 1)
\set per_part 10000 / :partitions
-- int() rounds to the nearest integer, so "- 0.5" turns it into floor(), as in ledger-batched.sql.
\set i int(:per_part * pow(random(0, 999999) / 1000000.0, 4) - 0.5)
\set mid :p + :partitions * :i
\set amount random(1000, 100000)
\set fee :amount / 50
BEGIN;
WITH pay AS (SELECT nextval('ledger_payment_seq') AS pid)
INSERT INTO ledger_postings (payment_id, account_id, amount)
SELECT pay.pid, v.account_id, v.amount
FROM pay, (VALUES (20000, -:amount), (:mid, :amount - :fee), (30000, :fee)) AS v (account_id, amount);
UPDATE ledger_accounts SET balance = balance + :amount - :fee WHERE id = :mid;
UPDATE ledger_accounts SET balance = balance - :amount WHERE id = 20000;
UPDATE ledger_accounts SET balance = balance + :fee WHERE id = 30000;
END;
