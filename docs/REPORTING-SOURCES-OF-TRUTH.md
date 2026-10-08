# PTG reporting sources of truth

This policy is intentionally read-only and does not switch any accounting feature flag,
backfill data, create a migration, or merge ledgers.

| Reporting concern | Authoritative reader/data | Excluded parallel source |
| --- | --- | --- |
| Operational party statement and receivable/payable | `PartyStatementReadService` and `PartyBalanceReadService`; `LedgerEntry`, payment and sarraf settlement flows | Independent debit/credit formulas in controllers |
| Statutory accounting, trial balance and fiscal-year close | Posted `JournalEntry` and `JournalLine` | Legacy `LedgerEntry` |
| Physical stock and stock card | `IStockService` over `InventoryMovement` | Sales/loading totals reconstructed outside stock |
| Cost of a sale (company, shipment, contract journey) | `ProfitAndLossService.SaleCost`: sale share attributed by `LoadPurchaseContractSalesAsync` × that contract's `BuildPurchaseCostBasisAsync` unit cost **as of the sale date**; only a sale without such a cost takes its active `SalesCostConsumption`; never both | Unit cost as of the report end date (look-ahead), current replacement price, pool + contract cost added together |
| Realised sales P&L per sale/shipment/contract journey | `ProfitAndLossService.BuildForSalesAsync`/`BuildForSaleGroupsAsync`; contract journey uses `BuildForPurchaseContractSalesAsync` (that contract's share only) | Per-page COGS formulas |
| Company period performance (Company Balance, Company Financial Status, mobile today profit) | `ProfitAndLossService.BuildCompanyPeriodAsync`: sales, operating expenses and realised FX from `BuildCompanyAsync`; COGS from the sale-cost rule above; plus undocumented loading warehouse/other in the loading's period | `BuildCompanyAsync` pool-only COGS, contract lifecycle loading/loss totals, any controller-level profit formula |
| Contract profit | `ProfitAndLossService.BuildContractEconomicsAsync` | Per-page contract formulas |
| Goods in transit quantity (report, mobile, Company Balance) | `GoodsInTransitQuantityReader` as of a date | Per-page remaining-quantity formulas |
| Contract/shipment operational lifecycle | Contract, shipment, loading, transport, customs, expense and loss entities | Accounting journal unless the report is explicitly statutory |

## Cutover rule

`LedgerEntry` is the operational compatibility ledger currently consumed by party
statements. `JournalEntry`/`JournalLine` is the independent accounting core used by
trial balance and fiscal-year workflows. They can contain the same underlying event.
No management report may add or union both sources. Moving a report from legacy
ledger to journal requires a separately approved reconciliation/backfill plan and
is deliberately outside this change.

## Confidence rule

Sales without an active `SalesCostConsumption` row are reported as `NeedsReview`.
Their revenue remains visible, but zero COGS must not be interpreted as verified
profit. Cancelled sales/expenses and reversed cost-consumption rows are excluded.
A sale costed from the purchase-contract unit cost is `Estimated`; a sale costed only
from the pool is `Verified`; a sale with neither stays `NeedsReview` and the period
profit is not published as final.

## Time rule

UTC remains the persistence convention. `IAfghanistanBusinessClock` is the shared
boundary for “today” and converts a Kabul local calendar date to a half-open UTC
range. Date-only business columns continue to be compared as business dates.
