-- Invariant checks after a run: the first three queries must return 0.

-- 1. Double entry: the postings of every payment sum to zero.
SELECT count(*) AS unbalanced_payments
FROM (SELECT payment_id FROM ledger_postings GROUP BY payment_id HAVING sum(amount) <> 0) AS t;

-- 2. Every balance equals the sum of its postings.
SELECT count(*) AS mismatched_accounts
FROM ledger_accounts AS a
LEFT JOIN (SELECT account_id, sum(amount) AS total FROM ledger_postings GROUP BY account_id) AS p
       ON p.account_id = a.id
WHERE a.balance <> coalesce(p.total, 0);

-- 3. All balances together sum to zero.
SELECT coalesce(sum(balance), 0) AS total_balance FROM ledger_accounts;

-- Volume of the run: three postings per payment.
SELECT count(*) AS postings, count(*) / 3 AS payments FROM ledger_postings;
