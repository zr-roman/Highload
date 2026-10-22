-- Run once: psql -U postgres -f db\create-databases.sql
-- Each service owns its database; the services create their tables at startup.
CREATE DATABASE payments;
CREATE DATABASE ledger;
