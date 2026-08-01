-- Backfill missing "surplus" (اضافه‌بار) LossEvents for truck dispatches.
--
-- WHY
-- The freight-settlement path (TruckSettlementsController.SettleDispatchFreightAsync)
-- used to clamp the variance with Math.Max(loaded - discharged, 0) and only wrote a
-- LossEvent when that clamped value was > 0. A dispatch that discharged MORE than it
-- loaded therefore stored ShortageMt = 0 and produced NO LossEvent at all, so it never
-- appeared in "راپور کسری و اضافه‌بار حمل" (which reads LossEvents only).
-- The code now stores the signed difference and upserts a LossEvent for every non-zero
-- variance; this script repairs the rows written before that fix.
--
-- WHAT IS RECOVERABLE
-- Everything needed is still stored on the dispatch itself:
--   expected = LoadedQuantityMt - (sum of legacy TRUCK-ARRIVAL partial discharges)
--   actual   = DischargedQuantityMt
-- so a dispatch with a non-NULL DischargedQuantityMt is fully recoverable.
-- A dispatch with DischargedQuantityMt IS NULL has no measured discharge weight; the
-- variance cannot be reconstructed and is reported by section (3) below instead.
--
-- SAFETY
--   * Read-only for every existing row except TruckDispatches."ShortageMt"/"ChargeableShortageMt",
--     which are only re-derived from LoadedQuantityMt/DischargedQuantityMt (no freight,
--     no payable, no expense, no ledger, no inventory movement is touched).
--   * AffectsInventory = false and InventoryMovementId = NULL — these records are
--     traceability only; stock is NOT changed.
--   * ChargeableLossMt = 0 — surplus is never charged to the driver.
--   * Idempotent: skips dispatches that already have a non-cancelled DispatchShortage
--     LossEvent, so re-running creates nothing new.
--   * Reversible: DELETE FROM "LossEvents" WHERE "Reference" = 'BACKFILL-DISPATCH-SURPLUS';
--
-- RUN sections (1) and (3) first to review, then (2) to apply.

BEGIN;

-- ── (1) PREVIEW — recoverable dispatches that are missing their surplus record ──
WITH arrivals AS (
    SELECT d."Id" AS dispatch_id,
           COALESCE(SUM(m."QuantityMt"), 0) AS arrived_mt
    FROM "TruckDispatches" d
    LEFT JOIN "InventoryMovements" m
           ON m."Direction" = 1
          AND m."ReferenceDocument" LIKE 'TRUCK-ARRIVAL:' || d."Id" || ':%'
    GROUP BY d."Id"
),
candidates AS (
    SELECT d."Id",
           d."ProductId",
           d."ContractId",
           d."DriverId",
           d."LoadedQuantityMt",
           d."DischargedQuantityMt",
           ROUND(d."LoadedQuantityMt" - a.arrived_mt, 4) AS expected_mt,
           ROUND((d."LoadedQuantityMt" - a.arrived_mt) - d."DischargedQuantityMt", 4) AS difference_mt,
           COALESCE(d."FreightSettledDate", d."DispatchDate") AS event_date
    FROM "TruckDispatches" d
    JOIN arrivals a ON a.dispatch_id = d."Id"
    WHERE d."Status" <> 4                       -- not cancelled
      AND d."DischargedQuantityMt" IS NOT NULL
      AND NOT EXISTS (
            SELECT 1 FROM "LossEvents" le
            WHERE le."TruckDispatchId" = d."Id"
              AND le."Stage" = 5                -- LossEventStage.DispatchShortage
              AND le."IsCancelled" = false)
)
SELECT "Id"            AS dispatch_id,
       "LoadedQuantityMt",
       expected_mt,
       "DischargedQuantityMt",
       difference_mt,
       CASE WHEN difference_mt < 0 THEN 'اضافه‌بار' ELSE 'کسری' END AS kind
FROM candidates
WHERE ABS(difference_mt) >= 0.00005            -- TransportVarianceMath.Epsilon
ORDER BY "Id";

-- ── (2) APPLY ──
WITH arrivals AS (
    SELECT d."Id" AS dispatch_id,
           COALESCE(SUM(m."QuantityMt"), 0) AS arrived_mt
    FROM "TruckDispatches" d
    LEFT JOIN "InventoryMovements" m
           ON m."Direction" = 1
          AND m."ReferenceDocument" LIKE 'TRUCK-ARRIVAL:' || d."Id" || ':%'
    GROUP BY d."Id"
),
candidates AS (
    SELECT d."Id",
           d."ProductId",
           d."ContractId",
           d."DriverId",
           ROUND(d."LoadedQuantityMt" - a.arrived_mt, 4) AS expected_mt,
           d."DischargedQuantityMt" AS actual_mt,
           ROUND((d."LoadedQuantityMt" - a.arrived_mt) - d."DischargedQuantityMt", 4) AS difference_mt,
           COALESCE(d."ToleranceMt", d."AllowanceMt", 0) AS tolerance_mt,
           COALESCE(d."FreightSettledDate", d."DispatchDate") AS event_date
    FROM "TruckDispatches" d
    JOIN arrivals a ON a.dispatch_id = d."Id"
    WHERE d."Status" <> 4
      AND d."DischargedQuantityMt" IS NOT NULL
      AND NOT EXISTS (
            SELECT 1 FROM "LossEvents" le
            WHERE le."TruckDispatchId" = d."Id"
              AND le."Stage" = 5
              AND le."IsCancelled" = false)
)
INSERT INTO "LossEvents" (
    "Stage", "ProductId", "ContractId", "TruckDispatchId",
    "EventDate", "ExpectedQuantityMt", "ActualQuantityMt", "DifferenceQuantityMt",
    "ToleranceQuantityMt", "AllowableLossMt", "ChargeableLossMt",
    "ResponsiblePartyType", "ResponsiblePartyName", "FinancialTreatment",
    "AffectsInventory", "InventoryMovementId", "Reference", "Notes",
    "IsCancelled", "CreatedAtUtc")
SELECT 5,
       c."ProductId",
       c."ContractId",
       c."Id",
       c.event_date,
       c.expected_mt,
       c.actual_mt,
       c.difference_mt,
       c.tolerance_mt,
       -- allowable = min(positive part of the difference, tolerance)
       LEAST(GREATEST(c.difference_mt, 0), c.tolerance_mt),
       -- chargeable = max(0, positive part - tolerance) ⇒ always 0 for a surplus
       GREATEST(GREATEST(c.difference_mt, 0) - c.tolerance_mt, 0),
       'Driver',
       dr."FullName",
       CASE WHEN c.difference_mt < 0
            THEN 'Truck unload positive variance (surplus). Never charged to the driver.'
            ELSE 'Backfilled from settled dispatch weights.'
       END,
       false,
       NULL,
       'BACKFILL-DISPATCH-SURPLUS',
       'Backfill: رکورد تفاوت حمل از وزن بارگیری و تخلیهٔ ثبت‌شدهٔ همین ارسال بازسازی شد.',
       false,
       NOW() AT TIME ZONE 'utc'
FROM candidates c
LEFT JOIN "Drivers" dr ON dr."Id" = c."DriverId"
WHERE ABS(c.difference_mt) >= 0.00005;

-- Re-derive the signed variance columns on the dispatch itself so the dispatch row and
-- its LossEvent agree (the old code clamped a surplus to 0 here as well).
UPDATE "TruckDispatches" d
SET "ShortageMt" = le."DifferenceQuantityMt",
    "ChargeableShortageMt" = le."ChargeableLossMt",
    "UpdatedAtUtc" = NOW() AT TIME ZONE 'utc'
FROM "LossEvents" le
WHERE le."TruckDispatchId" = d."Id"
  AND le."Reference" = 'BACKFILL-DISPATCH-SURPLUS'
  AND le."Stage" = 5
  AND le."IsCancelled" = false
  AND d."ShortageMt" IS DISTINCT FROM le."DifferenceQuantityMt";

-- ── (3) NOT RECOVERABLE — no measured discharge weight was ever stored ──
SELECT d."Id" AS dispatch_id,
       d."DispatchDate",
       d."Status",
       d."LoadedQuantityMt",
       d."IsFreightSettled",
       'DischargedQuantityMt IS NULL — وزن تخلیه ثبت نشده؛ تفاوت قابل بازسازی نیست' AS reason
FROM "TruckDispatches" d
WHERE d."Status" <> 4
  AND d."DischargedQuantityMt" IS NULL
  AND NOT EXISTS (
        SELECT 1 FROM "LossEvents" le
        WHERE le."TruckDispatchId" = d."Id"
          AND le."Stage" = 5
          AND le."IsCancelled" = false)
ORDER BY d."Id";

COMMIT;
