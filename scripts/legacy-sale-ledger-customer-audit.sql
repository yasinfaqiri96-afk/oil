-- =====================================================================================
-- Legacy sale ledger rows without CustomerId — audit (READ ONLY)
--
-- WHY THIS EXISTS
--   SaleLedgerFactory writes every "Sale" LedgerEntry with CustomerId = SalesTransaction.CustomerId
--   and, for most sale paths, ContractId = the SOURCE PURCHASE contract. Older rows (before the
--   factory, or imports) can carry ContractId but no CustomerId. The supplier ownership rule used
--   to infer a supplier through that purchase contract, so such a row landed in the customer's
--   account (through the sale document) AND in the supplier's account.
--
--   Since the Financial Integrity work the read path never infers a supplier for SourceType 'Sale'
--   (LedgerEntryOwnership.SaleSourceType). The owner is the sale document's customer, which is
--   mandatory. So balances are correct WITHOUT changing data. This script only shows how many
--   rows exist, so the team can decide whether to also stamp CustomerId on the rows for clarity.
--
--   No UPDATE / INSERT / DELETE runs below. The optional backfill at the end is commented out.
--
-- HOW TO RUN
--   psql "<connection string>" -v ON_ERROR_STOP=1 -f scripts/legacy-sale-ledger-customer-audit.sql
-- =====================================================================================

SET default_transaction_read_only = on;

-- 1) How many sale ledger rows, and how many lack CustomerId.
SELECT count(*)                                              AS sale_ledger_rows,
       count(*) FILTER (WHERE l."CustomerId" IS NULL)        AS without_customer,
       round(sum(l."AmountUsd") FILTER (WHERE l."CustomerId" IS NULL), 2) AS without_customer_usd
FROM "LedgerEntries" l
WHERE l."SourceType" = 'Sale';

-- 2) Classification of the rows without CustomerId.
--    OWNER_FROM_SALE      the sale document exists: its CustomerId is the owner (safe to stamp).
--    SALE_CANCELLED       the sale is cancelled: row should be neutralised by its reversal.
--    ORPHAN               no sale document with that SourceId: needs a human decision.
--    SUPPLIER_LEAK_BEFORE row sat on a purchase contract with no SupplierId, so the old rule
--                         also showed it in that contract's supplier account.
SELECT CASE
           WHEN s."Id" IS NULL THEN 'ORPHAN'
           WHEN s."IsCancelled" THEN 'SALE_CANCELLED'
           ELSE 'OWNER_FROM_SALE'
       END                                                   AS classification,
       (l."Reference" LIKE '%-CANCEL')                       AS is_reversal_row,
       (c."ContractType" = 1 AND l."SupplierId" IS NULL)     AS supplier_leak_before,
       count(*)                                              AS rows,
       round(sum(l."AmountUsd"), 2)                          AS amount_usd,
       min(l."EntryDate")::date                              AS first_date,
       max(l."EntryDate")::date                              AS last_date
FROM "LedgerEntries" l
LEFT JOIN "SalesTransactions" s ON s."Id" = l."SourceId"
LEFT JOIN "Contracts" c ON c."Id" = l."ContractId"
WHERE l."SourceType" = 'Sale' AND l."CustomerId" IS NULL
GROUP BY 1, 2, 3
ORDER BY rows DESC;

-- 3) Row-level detail for review (limit as needed).
SELECT l."Id" AS ledger_id, l."EntryDate"::date, l."Side", l."AmountUsd", l."Reference",
       l."ContractId", c."ContractNumber", c."ContractType",
       s."Id" AS sale_id, s."CustomerId" AS sale_customer_id, s."IsCancelled" AS sale_cancelled
FROM "LedgerEntries" l
LEFT JOIN "SalesTransactions" s ON s."Id" = l."SourceId"
LEFT JOIN "Contracts" c ON c."Id" = l."ContractId"
WHERE l."SourceType" = 'Sale' AND l."CustomerId" IS NULL
ORDER BY l."EntryDate", l."Id"
LIMIT 500;

-- -------------------------------------------------------------------------------------
-- OPTIONAL, NOT EXECUTED: stamp CustomerId from the sale document (OWNER_FROM_SALE only).
-- Idempotent (touches only rows still NULL). Balances do not change: the read path already
-- uses the sale's customer. Take a backup, run in a transaction, review the count, then COMMIT.
--
-- BEGIN;
-- SET LOCAL default_transaction_read_only = off;
-- UPDATE "LedgerEntries" l
-- SET "CustomerId" = s."CustomerId"
-- FROM "SalesTransactions" s
-- WHERE l."SourceType" = 'Sale'
--   AND l."CustomerId" IS NULL
--   AND s."Id" = l."SourceId";
-- -- review the reported row count, then:
-- -- COMMIT;   -- or ROLLBACK;
-- -------------------------------------------------------------------------------------
