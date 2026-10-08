# MASHAL partner reporting continuation — 2026-10-08

The existing phase-one company balance, company-claim sign conversion and
unsold-cost allocation in the internal partnership statement are preserved.
No financial documents, database schema or persisted accounting entries are changed.

## Company claims versus internal partnership position

`PartnerCompanyBalanceReader.ReadEventsAsync` supplies company balance and
receivable/payable reports. The aging report, financial overview and Excel/PDF
exports consume those same report rows. Positive partner-account amounts are
company payables; negative amounts are company receivables. Book-owner balances
remain equity and are excluded from external claims.

Real funding comes from the existing `PartnerFundingReader`. Customer receipts
and refunds are handled separately: customer payment kind, customer identity,
identified partner and absence of a company cash account are required.
`FundingSource.Partner` alone and `SaleProceedsHolderPartnerId` alone never prove
that cash was received. A credit sale remains with the customer. Receipt payments
are counted once, even when cash applications also exist. Reversing an application
does not reverse receipt custody; an actual refund payment does.

Profit uses the existing contract economics cost, expense and FX policy, with an
optional source-event cutoff used only by company partner reporting. Each sale
gets its own revenue less its quantity share of realized contract costs; realized
FX is apportioned by revenue. Each sale's margin is split using the share history
at that sale's date. Existing residual-cent allocation is reused. No sale means no
realized profit event. These are calculated economic shares, not newly posted
profit-allocation journals. The internal partner profile and detailed partnership
statement retain their existing allocation convention, so their net positions
can intentionally differ from company claims.

Reversed settlements with timestamps retain the original event and add the
opposite event on the reversal date. Legacy reversed settlements without a
timestamp are excluded because no reversal date can be reconstructed safely.
Reports use currently stored documents. An edit/deletion that overwrote historical
document facts cannot be reconstructed by a read-only report without an audited
historical document source. No such history is invented here.

## Account age

The existing `PartyAging` route is kept. The visible name is account age since last
movement, not contractual maturity. No due date is inferred. Unknown movement
dates have their own category and blank day count. Excel and PDF receive the same
rows, totals and basis explanation as the page.

## Verification artifacts

Local logs and TRX results are under `.artifacts/mashal-*.log` and
`tests/PTGOilSystem.Web.Tests/TestResults/mashal-*.trx`. The P-001 regression
asserts supplier payable 90,000 USD, transport payable 280 USD, partner payable
zero, goods value 90,000 USD and company net balance -2,780 USD. This is a
reproducible test scenario; production financial documents are not accessed.

Initial targeted baseline: 47 passed, zero failed. The completed targeted run:
327 passed, zero failed, including the new PostgreSQL integration scenario.
Final build: zero errors and 13 warnings in unrelated existing files. Full suite:
3,932 passed, three failed, zero skipped; total 3,935, duration 13 minutes 46 seconds.

The full run also checks existing UI source contracts. The following failures
reference views and tests not changed by this continuation:

- `ContractJourneyViewStructureTests.Loading_Create_Exposes_Excel_Transport_And_Cost_Fields`:
  missing `ModalTarget = "loadingIndexExpensesModal"` in the loading index view.
- `ContractJourneyViewStructureTests.InventoryTransportReceipt_Focused_DirectSale_Keeps_Required_Fields_Clear_And_Optional_Sections_Closed`:
  expected `SaleCustomerId` label markup differs in the receipt create view.
- `ShellViewStructureTests.Contracts_List_Uses_The_Shared_Row_Selection_Contract`:
  expected inline delete form markup differs in the contracts index view.

Those unrelated views and test expectations are preserved. Final full-run counts
are recorded in `mashal-full.trx`; the full suite is not completely passing.

## Files touched by this continuation

- `src/PTGOilSystem.Web/Services/PartyStatements/PartnerCompanyBalanceReader.cs`
- `src/PTGOilSystem.Web/Services/PartyStatements/PartyBalanceReadService.cs`
- `src/PTGOilSystem.Web/Services/Reporting/ProfitAndLossService.cs`
- `src/PTGOilSystem.Web/Services/Reporting/ProfitAndLossService.ContractEconomics.cs`
- `src/PTGOilSystem.Web/Services/PurchaseAggregationService.cs`
- `src/PTGOilSystem.Web/Services/SaleContractAttributionReader.cs`
- `src/PTGOilSystem.Web/Controllers/ReportsController.cs`
- `src/PTGOilSystem.Web/Controllers/ReportsController.PartyAging.cs`
- `src/PTGOilSystem.Web/Controllers/ReportsController.Export.cs`
- `src/PTGOilSystem.Web/Models/Reports/PartyAgingViewModels.cs`
- `src/PTGOilSystem.Web/Views/Reports/PartyAging.cshtml`
- `src/PTGOilSystem.Web/Views/Reports/CompanyOverview.cshtml`
- `src/PTGOilSystem.Web/Views/Reports/ReceivablesPayables.cshtml`
- `tests/PTGOilSystem.Web.Tests/CompanyBalancePartnerTests.cs`
- `tests/PTGOilSystem.Web.Tests/PartnerAccountingUnificationTests.cs`
- `tests/PTGOilSystem.Web.Tests/PartnerFundingTests.cs`
- `tests/PTGOilSystem.Web.Tests/PartnerCompanyBalancePostgreSqlTests.cs`
- `docs/MASHAL-PARTNER-REPORTING.md`
