# MASHAL local integration progress — 2026-10-10

Local workspace: `F:\New folder\oil-main` (origin `yasinfaqiri96-afk/oil`).
Integration branch: `local/mashal-complete-integration` (no upstream; nothing pushed).

## Initial local state

- Branch `master` at `df96f9a` (= `origin/main`); other local branches `fix/statements-loaded-value-reversal`, `fix/zuri-contract-summary-p002`; one worktree; `stash@{0}` (codex-safe-pull-2026-09-15).
- Uncommitted user work kept as-is (not committed by the integration): `src/PTGOilSystem.Web/wwwroot/css/ptg/11-details.css` (journey section/badge styling), two tracked files under `ptg-backup-20260822-210917-manual/` and one untracked backup zip there.
- Recovery point outside the repository: `F:\New folder\mashal-local-backup-20261010T142848` (verified `git bundle --all`, binary working-tree patch, copies of modified/untracked files, status/branch/stash listings).

## Branch analysis (after `git fetch origin`)

All specialist branches fork from `73344be`. `origin/main` has one commit after it (`df96f9a`), already present in the integrity branch as `5a507ee` (identical patch id).

| Branch | Head | Commits not in integrity | Action |
| --- | --- | --- | --- |
| `fix/mashal-integrity-workflows` | `76a8daf` | — (base, 71 commits after `73344be`) | Integration base |
| `fix/mashal-backfill` | `daedecd` | 0 of 7 (all patch-equivalent) | Nothing to integrate |
| `fix/mashal-expenses-finance` | `dfe771c` | 0 of 15 | Nothing to integrate |
| `fix/mashal-receipt-concurrency` | `e769fd1` | 0 of 10 | Nothing to integrate |
| `fix/mashal-ux` | `1b47754` | 0 of 5 | Nothing to integrate |
| `fix/mashal-sales-sources` | `b546a85` | 3 (`6680a16`, `2e15225`, `b546a85`) | Cherry-picked (`cf0f05e`, `0e6a409`, `48294bb`) |
| `fix/mashal-qa` | `5b516de` | 3 new tests (`1d73885`, `a8807b6`, `5b516de`); its other commits are conflict-resolved copies merged by `0a884c3` | Merged (`6df5a72`); net change: two test files |
| `main` | `df96f9a` | 0 | Already included |

Conflict: `CargoSourceQueryService.OtherSources.cs` (dispatch `SupportedActions`) — both sides were logically identical (parenthesization only); the cherry-picked form was kept.

## Fixes made locally

- `a7eb2ff` test: the cherry-picked batch sale cancellation PostgreSQL test referenced a non-existent `InventoryValuationPools` set and never compiled; it now uses `InventoryAverageCosts`.
- `BulkFromLoadingConcurrencyTests.A_Fully_Committed_Filter_Request_Replays_Without_An_Empty_Selection_Error`: the seeded truck loadings had no vehicle, so filter rows were correctly rejected (`TRANSPORT_RECEIPT_VEHICLE_INVALID`). The test now seeds the loading truck. Product behavior unchanged.

## Validation so far

- Solution build (`dotnet build ptg-oil-system.sln -c Debug --no-restore`): 0 errors after `a7eb2ff`.
- `dotnet ef migrations has-pending-model-changes`: no pending model changes.
- New migration in scope: `20261009235000_WidenSupplierAllocationFxPrecision` (widens four allocation FX columns to `numeric(24,12)`; guarded `Down`). Not applied to any non-test database.
- First full-suite run: one failure (above, fixed). The run then spent more than 30 minutes inside `Expense_Thousand_Loadings_Preserves_Payable_Source_And_Accounting(accountingEnabled: true)` — the database was idle in one transaction while the test host consumed CPU (EF change tracking). See "Open items".

## Resume session — 2026-10-10 (after the usage limit)

State recovered: branch and commits intact, the stalled suite host was gone, `full-tests.log` was not found. A user-run `scripts/run-local.ps1` (port 5000) holds `src/PTGOilSystem.Web/bin`, so verification builds use an isolated output: `dotnet build tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --artifacts-path .artifacts/mashal` (ignored by Git). Uncommitted UI edits found in the tree (`Views/*/CreateGroup.cshtml`, CSS 11/45/50/64) are not part of this work and are left uncommitted.

### Filter replay test (`7cacb5c`)

Root cause: the shared seed creates truck loadings without `TruckId`; filter rows take each loading's own vehicle, so every row failed `TRANSPORT_RECEIPT_VEHICLE_INVALID`, `CompletedCount == 0`, and the first request rendered the view. ModelState, form token, `ProcessedFormTokens` recovery and retry logic were correct. Only the seed changed; assertions unchanged. `BulkFromLoadingConcurrencyTests`: 13/13 passed.

### Group expense / sale scaling

Measured with `PTG_PERF_ROWS` (benchmark row override) and new DetectChanges counters in `DatabaseOperationPerformanceTests`:

| Operation | Before | After |
| --- | --- | --- |
| Group expense, 100 rows, accounting | 41.1 s, 6,940 DetectChanges passes (36.5 s), 6.0 GB allocated | — |
| Group expense, 200 rows, accounting | 155 s | 6.9 s |
| Group sale, 200 rows, accounting | 61 s (after first fix) | 9.6 s |
| Group expense, 1,000 rows, accounting | > 30 min (stalled) | 26.9 s, peak tracked 8 |
| Group expense, 1,000 rows, no accounting | ≈ 25 min (extrapolated) | 6.0 s |
| Group sale, 1,000 rows, accounting | ≈ 25 min (extrapolated) | 57.1 s, peak tracked 14 |
| Group sale, 1,000 rows, no accounting | — | 10.3 s |

Root causes (all EF change tracking, not PostgreSQL — the DB sat idle in the transaction):

1. `ApplicationDbContext` overrode both `SaveChangesAsync(ct)` and `SaveChangesAsync(bool, ct)`; the base of the first calls the second, so `PrepareTrackedEntitiesForSave` ran twice per save, and each of its six `ChangeTracker.Entries()` calls ran a full DetectChanges (≈14 passes per save). Now: one override pair, one explicit DetectChanges, the stamp/guard steps run with auto-detect off; the save's own pass still picks up stamped values.
2. Group expense lock queries (`SELECT * … FOR UPDATE` → `ToListAsync`) tracked every locked loading/leg/dispatch; they now use the lock-only `ExecuteSqlInterpolatedAsync("SELECT 1 … FOR UPDATE")` form already used by group sale.
3. Each row's saved documents (expense/sale, receipt, allocation, ledger, journals, audit) stayed tracked for the whole transaction. `SavedRowTrackingScope` detaches only entities the row added and already saved; pending changes and query-loaded entities stay tracked. The expense batch USD total now sums the saved rows in the same transaction (rows are already rounded to 4 decimals, so the value is identical).

Accounting, ledger, audit and transaction boundaries are unchanged; all benchmark assertions (counts, totals, journal balance, source lineage) pass at 1,000 rows.

`Receive_Thousand_Loadings…(accounting: true)` failed independently of this work: the QA benchmark predates `7798bb9` (a receipt journal requires the posted purchase). Its setup now posts the purchases first, like the sale benchmark; assertions unchanged.

### Cancellation integrity at volume

New PostgreSQL tests create a 1,000-row group (with and without accounting) and cancel it through the controllers. They require: every sale, receipt and allocation cancelled; each source's ledger row paired with one equal opposite row; every journal with exactly one reversal and every account netting to zero; purchase journals untouched; each loading's full remainder sellable again; no inventory movement. Expense: every share cancelled, ledger paired, journals reversed, no payment created.

Group cancellation had the same tracking growth (100-row sale cancel with accounting: 20.7 s, 1,302 tracked). Sale and expense group cancellation now read IDs without tracking, load each line under the existing locks inside a `SavedRowTrackingScope`, and release it once saved.

| Cancel 1,000 rows | Time | Peak tracked |
| --- | --- | --- |
| Group sale, no accounting | 34.0 s | 8 |
| Group sale, accounting | 73.1 s | 14 |
| Group expense, no accounting | 4.5 s | 3 |
| Group expense, accounting | 20.4 s | 6 |

`SalesAccountingAdapterTests.Direct_Loading_Cogs_Uses_Posted_Purchase_And_Reverses_Without_Inventory_Pool` failed before this work: it looked for the COGS reversal by `SourceEntityType = SalesTransaction`, but every reversal journal's source entity is the reversed journal (`AccountingPostingService.ReverseAsync`, unchanged since July). It now finds both journals by their COGS source events; the in-transit net-zero assertion is unchanged.

### Branch re-check

`git fetch` shows the same heads as above. `git cherry`: integrity, backfill, expenses-finance, receipt-concurrency, ux, qa and main have nothing missing. `sales-sources` shows two "+" commits only because of conflict resolution; `git range-diff` confirms their content is present (`LossMode = ImmediateKnownLoss` already came from `76a8daf`; the `SupportedActions` change is parenthesization only).

## Open items / exact resume point

1. Finish the full-suite run and record TRX counts; rebuild tests (the stalled host holds the test `bin` DLLs).
2. Measure the 1,000-row group expense (and group sale) with accounting enabled and determine the dominant cost before changing code.
3. Local preview on a disposable simulated database (`ptg_oil_accounting_test_mashal_preview`), then `tests/browser/integrated-journey-responsive.cjs` at 1440/1024/768/390.
4. Known baseline limitation: 18 `PartnerSettlementImport` cases need the external `Payment.xlsx` fixture, which is not in the repository.
