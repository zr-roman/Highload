-- Runs after schema.sql has committed, outside its transaction (DatabaseSchema.ApplyAsync).
--
-- Reads the rows written before the CHECK of the statuses was replaced NOT VALID (schema.sql) and marks it valid.
-- It scans all of payments, but under SHARE UPDATE EXCLUSIVE: inserts and updates go on, and of the other replicas
-- starting at the same time one validates and the others wait for it. On a validated constraint, as on a database
-- created with the new CHECK, it is a no-op.
ALTER TABLE payments VALIDATE CONSTRAINT payments_status_check;
