# PTG reporting sources of truth

This policy is intentionally read-only and does not switch any accounting feature flag,
backfill data, create a migration, or merge ledgers.

| Reporting concern | Authoritative reader/data | Excluded parallel source |
| --- | --- | --- |
| Operational party statement and receivable/payable | `PartyStatementReadService` and `PartyBalanceReadService`; `LedgerEntry`, payment and sarraf settlement flows | Independent debit/credit formulas in controllers |
| Statutory accounting, trial balance and fiscal-year close | Posted `JournalEntry` and `JournalLine` | Legacy `LedgerEntry` |
| Physical stock and stock card | `IStockService` over `InventoryMovement` | Sales/loading totals reconstructed outside stock |
| Realised sales P&L | `ProfitAndLossService`; non-cancelled `SalesTransaction`, active `SalesCostConsumption`, non-cancelled `ExpenseTransaction` | Current replacement price or purchase-contract value guessed as COGS |
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

## Time rule

UTC remains the persistence convention. `IAfghanistanBusinessClock` is the shared
boundary for “today” and converts a Kabul local calendar date to a half-open UTC
range. Date-only business columns continue to be compared as business dates.
