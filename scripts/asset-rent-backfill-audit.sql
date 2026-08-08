-- =====================================================================================
-- Operational asset rent — backfill audit (READ ONLY)
--
-- WHY THIS EXISTS
--   Manual external asset rent started producing a LedgerEntry only from the AssetRent
--   phase onwards. Rows recorded before that are sitting in AssetRentTransactions with
--   IsPostedToLedger = false and no ledger row. Some of them SHOULD have a ledger row,
--   some must never get one, and some cannot be decided from data alone.
--
--   This script does not change anything. It only classifies. Run it, read it, decide.
--   No UPDATE / INSERT / DELETE appears anywhere below, on purpose.
--
-- HOW TO RUN
--   psql "<connection string>" -f scripts/asset-rent-backfill-audit.sql
--   Add \copy (…) TO 'audit.csv' CSV HEADER around the final SELECT to export it.
--
-- CLASSIFICATION — mirrors AssetRentPostingPolicy.ResolveSkipReason exactly.
--   Keep the two in sync: if the policy changes, this script must change with it.
--
--     CANCELLED                  IsCancelled = true.
--                                Needs a reversal only if it already has a Credit row.
--     ALREADY_ACCOUNTED_ELSEWHERE Any of LoadingRegisterId / TransportLegId /
--                                InventoryTransportReceiptId / TruckDispatchId is set.
--                                System generated: the freight/expense counterpart is
--                                already posted from the operational side, so posting
--                                the rent as well would count the same money twice.
--     INTERNAL_ONLY              UsageType = InternalCompanyUse (1) or
--                                ChargedToType = CompanyInternal (4).
--                                The company using its own asset is not external revenue.
--     PARTNER_UNSUPPORTED        UsageType = PartnerUse (3) or ChargedToType = Partner (5).
--                                LedgerEntry has no PartnerId column, so a partner rent
--                                cannot reach a party ledger without a schema change.
--     AMBIGUOUS                  Counterparty cannot be resolved, or the amount is not
--                                positive. Never touch these — fix the source record first.
--     SAFE_TO_POST               Everything else: a manual, non cancelled, positive rent
--                                with a resolvable counterparty and no ledger row yet.
--
--   Enum values used above (Models/Entities/OperationalAssets.cs):
--     AssetRentUsageType:    1 InternalCompanyUse, 2 ExternalCustomerRental,
--                            3 PartnerUse, 4 Other
--     AssetRentChargedToType: 1 PurchaseContract, 2 SalesContract, 3 Customer,
--                            4 CompanyInternal, 5 Partner, 6 Other
--
-- DOUBLE POSTING RISK
--   HIGH   — a ledger row already exists but the rent is not linked to it, or more than
--            one original row exists. Posting again would duplicate real money.
--   MEDIUM — a matching expense/freight row exists for the same asset, date and amount.
--            The same economic event may already be accounted for on the expense side.
--   NONE   — no ledger row and no matching expense row.
-- =====================================================================================

WITH rent_ledger AS (
    SELECT
        l."SourceId"                                                        AS rent_id,
        -- LedgerSide: Debit = 1, Credit = 2. کرایه با Credit ثبت و با Debit برگردانده می‌شود.
        COUNT(*) FILTER (WHERE l."Side" = 2)                                AS credit_rows,
        COUNT(*) FILTER (WHERE l."Side" = 1)                                AS debit_rows,
        COUNT(*)                                                            AS total_rows,
        MIN(l."Id") FILTER (WHERE l."Side" = 2)                             AS first_credit_ledger_id,
        SUM(l."AmountUsd") FILTER (WHERE l."Side" = 2)                      AS credit_amount_usd
    FROM "LedgerEntries" l
    WHERE l."SourceType" = 'AssetRent'
    GROUP BY l."SourceId"
),
-- «همان مبلغ از سمت مصرف هم ثبت شده؟» تطبیق محافظه‌کارانه: همان دارایی، همان روز، همان مبلغ.
-- این فقط یک هشدار برای بررسی دستی است، نه اثبات دوباره‌شماری.
expense_match AS (
    SELECT
        r."Id"                    AS rent_id,
        COUNT(e."Id")             AS matching_expense_count,
        MIN(e."Id")               AS first_matching_expense_id
    FROM "AssetRentTransactions" r
    LEFT JOIN "ExpenseTransactions" e
           ON e."OperationalAssetId" = r."OperationalAssetId"
          AND e."IsCancelled" = false
          AND e."ExpenseDate"::date = r."RentDate"::date
          AND ROUND(e."AmountUsd", 2) = ROUND(r."AmountUsd", 2)
    GROUP BY r."Id"
)
SELECT
    r."Id"                                    AS "RentId",
    r."RentDate"::date                        AS "Date",
    a."AssetCode"                             AS "Asset",
    r."UsageType"                             AS "UsageType",
    r."ChargedToType"                         AS "ChargedToType",
    r."ChargedToCustomerId"                   AS "Customer",
    r."ChargedToContractId"                   AS "Contract",
    r."ChargedToServiceProviderId"            AS "ServiceProvider",
    r."ChargedToPartnerId"                    AS "Partner",
    r."AmountOriginal"                        AS "OriginalAmount",
    r."Currency"                              AS "Currency",
    r."FxRateToUsd"                           AS "FxRate",
    r."AmountUsd"                             AS "AmountUsd",
    r."LoadingRegisterId"                     AS "LoadingRegisterId",
    r."TransportLegId"                        AS "TransportLegId",
    r."InventoryTransportReceiptId"           AS "InventoryTransportReceiptId",
    r."TruckDispatchId"                       AS "TruckDispatchId",
    r."IsCancelled"                           AS "IsCancelled",
    r."IsPostedToLedger"                      AS "IsPostedToLedger",
    r."LedgerEntryId"                         AS "LedgerEntryId",
    COALESCE(rl.total_rows, 0)                AS "ExistingMatchingLedgerCount",
    COALESCE(rl.credit_rows, 0)               AS "ExistingOriginalCount",
    COALESCE(rl.debit_rows, 0)                AS "ExistingReversalCount",
    rl.credit_amount_usd                      AS "ExistingLedgerAmountUsd",
    COALESCE(em.matching_expense_count, 0)    AS "PossibleMatchingExpenseCount",
    em.first_matching_expense_id              AS "PossibleMatchingExpenseId",

    CASE
        WHEN r."IsCancelled" THEN
            CASE
                WHEN COALESCE(rl.credit_rows, 0) > 0 AND COALESCE(rl.debit_rows, 0) = 0
                    THEN 'CANCELLED / needs reversal — cancelled rent still carries a posted balance'
                ELSE 'CANCELLED / no action'
            END
        WHEN r."LoadingRegisterId" IS NOT NULL
          OR r."TransportLegId" IS NOT NULL
          OR r."InventoryTransportReceiptId" IS NOT NULL
          OR r."TruckDispatchId" IS NOT NULL
            THEN 'ALREADY_ACCOUNTED_ELSEWHERE / no action'
        WHEN r."AmountUsd" <= 0 OR r."AmountOriginal" <= 0
            THEN 'AMBIGUOUS / fix amount at source first'
        WHEN r."UsageType" = 1 OR r."ChargedToType" = 4
            THEN 'INTERNAL_ONLY / no action'
        WHEN r."UsageType" = 3 OR r."ChargedToType" = 5
            THEN 'PARTNER_UNSUPPORTED / no action until partner ledger exists'
        WHEN r."ChargedToType" = 3 AND r."ChargedToCustomerId" IS NULL
            THEN 'AMBIGUOUS / customer rent without a customer'
        WHEN r."ChargedToType" IN (1, 2) AND r."ChargedToContractId" IS NULL
            THEN 'AMBIGUOUS / contract rent without a contract'
        WHEN r."ChargedToType" = 6
         AND r."ChargedToServiceProviderId" IS NULL
         AND r."ChargedToCustomerId" IS NULL
            THEN 'AMBIGUOUS / other-party rent without a counterparty'
        WHEN r."ChargedToType" NOT IN (1, 2, 3, 6)
            THEN 'AMBIGUOUS / unsupported charged-to type'
        WHEN COALESCE(rl.credit_rows, 0) > 0
            THEN 'ALREADY_POSTED / verify link only, do not post again'
        ELSE 'SAFE_TO_POST / eligible for the idempotent backfill'
    END                                       AS "RecommendedAction",

    CASE
        WHEN COALESCE(rl.credit_rows, 0) > 1 THEN 'HIGH'
        WHEN COALESCE(rl.credit_rows, 0) > 0
         AND (r."LedgerEntryId" IS NULL OR r."LedgerEntryId" <> rl.first_credit_ledger_id) THEN 'HIGH'
        WHEN COALESCE(em.matching_expense_count, 0) > 0 THEN 'MEDIUM'
        ELSE 'NONE'
    END                                       AS "DoublePostingRisk"

FROM "AssetRentTransactions" r
JOIN "OperationalAssets" a ON a."Id" = r."OperationalAssetId"
LEFT JOIN rent_ledger   rl ON rl.rent_id = r."Id"
LEFT JOIN expense_match em ON em.rent_id = r."Id"
ORDER BY r."RentDate" DESC, r."Id" DESC;
