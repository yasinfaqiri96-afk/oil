-- Backfill ShipmentId for INDIVIDUAL terminal-stock sales, deriving the shipment from the
-- physical provenance of the tank they were sold out of.
--
-- Context: BackfillGroupSaleShipmentId.sql covers group sales (SalesBatchId IS NOT NULL) and
-- derives the shipment from the source purchase contract. Both of those assumptions fail for
-- individual «موجودی مخزن» sales entered against a tank that was filled from a purchase
-- contract shared by more than one shipment:
--   • the sales have no SalesBatchId, so the group script skips them outright;
--   • their contract maps to several shipments, so contract -> shipment is ambiguous and both
--     the group script and ResolveShipmentIdForContractAsync correctly refuse to guess.
--
-- The tank itself is not ambiguous. Stock physically reaches a tank through
-- InventoryTransportReceipts, and every receipt carries a leg that knows its ShipmentId. When
-- all receipts that ever fed a tank come from ONE shipment, every sale out of that tank belongs
-- to that shipment. This script uses that provenance instead of the contract map.
--
-- Safe by design:
--   • Only touches SalesTransactions with ShipmentId IS NULL, not cancelled, not PreSale.
--   • Only tanks whose entire receipt history maps to exactly one shipment; anything mixed is
--     skipped rather than guessed.
--   • Only sales whose OUT movements all came from such tanks and agree on one shipment.
--   • Touches ShipmentId only. No amounts, no quantities, no InventoryMovements, no
--     LedgerEntries -- purely the reporting link the shipment file reads.
--   • Idempotent: re-running changes nothing once rows are tagged.
--
-- Review with the SELECT at the bottom before running.

BEGIN;

WITH tank_ship AS (
    -- Tanks fed by exactly one shipment across their whole receipt history.
    SELECT r."DestinationStorageTankId" AS "StorageTankId",
           MIN(l."ShipmentId")          AS "ShipmentId"
    FROM "InventoryTransportReceipts" r
    JOIN "InventoryTransportLegs" l ON l."Id" = r."InventoryTransportLegId"
    WHERE r."IsCancelled" = false
      AND r."DestinationStorageTankId" IS NOT NULL
      AND l."ShipmentId" IS NOT NULL
    GROUP BY r."DestinationStorageTankId"
    HAVING COUNT(DISTINCT l."ShipmentId") = 1
),
sale_ship AS (
    -- Each sale's OUT movements, mapped to the shipment of the tank they drew from.
    SELECT m."SalesTransactionId" AS "SaleId",
           ts."ShipmentId"        AS "ShipmentId"
    FROM "InventoryMovements" m
    JOIN tank_ship ts ON ts."StorageTankId" = m."StorageTankId"
    WHERE m."Direction" = 2            -- MovementDirection.Out
      AND m."SalesTransactionId" IS NOT NULL
    GROUP BY m."SalesTransactionId", ts."ShipmentId"
),
final AS (
    -- Keep only sales that resolve to a single shipment.
    SELECT "SaleId", MIN("ShipmentId") AS "ShipmentId"
    FROM sale_ship
    GROUP BY "SaleId"
    HAVING COUNT(DISTINCT "ShipmentId") = 1
)
UPDATE "SalesTransactions" s
SET "ShipmentId" = f."ShipmentId"
FROM final f
WHERE s."Id" = f."SaleId"
  AND s."ShipmentId" IS NULL
  AND s."IsCancelled" = false
  AND s."SaleStage" <> 1;             -- SaleStage.PreSale

-- Sharpen contract attribution too. The shipment file attributes a sale to a purchase contract
-- via SourcePurchaseContractId and, when it is NULL, spreads the quantity proportionally across
-- the shipment's contracts. The sale's own OUT movement already records the real contract, so
-- copy it wherever the sale drew from exactly ONE contract. Sales genuinely split across two
-- contracts are left NULL on purpose, so the existing proportional split still handles them.
WITH single_contract AS (
    SELECT m."SalesTransactionId" AS "SaleId",
           MIN(m."ContractId")    AS "ContractId"
    FROM "InventoryMovements" m
    WHERE m."Direction" = 2
      AND m."SalesTransactionId" IS NOT NULL
      AND m."ContractId" IS NOT NULL
    GROUP BY m."SalesTransactionId"
    HAVING COUNT(DISTINCT m."ContractId") = 1
)
UPDATE "SalesTransactions" s
SET "SourcePurchaseContractId" = sc."ContractId"
FROM single_contract sc
WHERE s."Id" = sc."SaleId"
  AND s."SourcePurchaseContractId" IS NULL
  AND s."IsCancelled" = false
  AND s."SaleStage" <> 1;

-- Anything still unlinked (mixed-provenance tanks, or sales with no OUT movement):
-- SELECT s."Id", s."InvoiceNumber", s."SaleStage", s."QuantityMt"
-- FROM "SalesTransactions" s
-- WHERE s."ShipmentId" IS NULL AND s."IsCancelled" = false AND s."SaleStage" <> 1;

COMMIT;
