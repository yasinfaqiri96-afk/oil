# Stock reconciliation — web dashboard vs `IStockService`

Question from Phase 1: the web dashboard sums tank movements itself, while
`docs/REPORTING-SOURCES-OF-TRUTH.md` names `IStockService` as the stock authority. Can the two numbers differ,
and which one should the mobile Home screen use?

Status: **investigated by reading code; no calculation was changed.** Not yet compared against production data.

## 1. How each number is produced

Both read only `InventoryMovements`. Signed quantity is the same in both places:
`In` and `Adjustment` add `QuantityMt`; `Out` and `Transfer` subtract `QuantityMt`.

| | Web dashboard `DashboardViewModel.TerminalStockMt` | `IStockService.GetTotalFreeQuantityMtAsync()` (mobile) |
|---|---|---|
| Code | `DashboardService.PopulateInventoryAsync` | `StockService.BuildMovementQuery` + `SumSignedQuantityAsync` |
| Rows | all movements | all movements (no terminal/date filter when called with no arguments) |
| Grouping | (ProductId, TerminalId, **movement.ContractId**), then summed | none (one sum) |
| Date boundary | none | `MovementDate <= asOfUtc` only when `asOfUtc` is passed (mobile passes none) |
| Cancelled / reversed | no status column; reversals are inverse movements and net out | same rows, same result |
| Reservations (pre-sale) | not subtracted | not subtracted |

**Result: the total is the same formula over the same rows. The two totals cannot differ.**
The Phase 1 note ("may differ") overstated the risk and has been corrected.

## 2. Where the web dashboard and the stock service really differ

These affect other dashboard values, not the total:

1. **Contract attribution of low-stock alert rows.** The dashboard groups by `movement.ContractId` only.
   `StockService.GetStockSummaryAsync` assigns a movement without `ContractId` to its loading receipt's
   contract (`LoadingReceipt.LoadingRegister.ContractId`). The same stock can therefore appear as a
   "no contract" alert row on the dashboard but as a contract row on the inventory page. Per-row quantities
   and alert rows can differ; the total cannot.
2. **`LowStockTankCount`.** Grouped by `StorageTankId` over all movements, counting tanks at ≤ 10 MT
   (including zero and negative, and tanks that are no longer active). Movements without a tank are not
   counted. `IStockService` has no equivalent all-tank method (`GetTankAvailabilityAsync` needs product +
   contract and drops non-positive rows). The mobile app shows the web dashboard's value unchanged.

## 3. Effects checked by flow

| Flow | Stock effect (both readers) | Notes |
|---|---|---|
| Loading receipt | `In` movements when the receipt is posted | Goods leave "goods in transit" (loaded − receipts − allocations − receipt shortage) at the same moment |
| Receipt cancellation | inverse movement via `InventoryMovementWriter.PostReversalAsync` | Nets out in both |
| Internal transport leg | outbound movement at leg load, inbound at transport receipt | The in-transit part is counted in goods in transit, not in stock |
| Loss events | `LossEventWorkflowService` writes movements only when a stock movement is required | Same rows for both readers |
| Sales / direct dispatch | movements are written directly in `SalesController`, `SalesController.Group`, `DispatchController`, `LoadingReceiptsController` | Same rows for both; exact in/out pairing of direct-from-receipt flows **not verified** in this phase |
| Allocations (`LoadingReceiptAllocation`) | trace records, not stock movements | No effect on either total |

## 4. Open items (not fixed — stock logic was out of scope)

1. **Reversing an `Adjustment` movement needs verification.** `InventoryMovementWriter.InvertDirection`
   maps `Adjustment → Adjustment` with the same positive quantity, so a reversal of an adjustment would add
   the quantity again instead of removing it. This would affect web and mobile the same way. It needs a
   test against the real reversal callers before any change.
2. **Customer-delivery goods in transit vs stock.** Goods in transit counts the full loaded quantity of
   loaded / in-transit truck dispatches. Whether every dispatch mode has already posted its `Out` movement
   (so the quantity is not also in stock) was not verified for `DirectFromReceipt`.
3. **Dashboard alert contract attribution** (section 2.1) could be aligned with `StockService`. That is a
   web dashboard behavior change and needs separate approval.

## 5. Decision for mobile

- Mobile `inventory.totalMt` uses `IStockService` — the documented source of truth, and numerically
  identical to the web dashboard total.
- Mobile `inventory.lowStockTankCount` is the web dashboard value, labelled as such in the API model.
- The app shows inventory and goods in transit as separate cards and never adds them.
- No web calculation was modified.
