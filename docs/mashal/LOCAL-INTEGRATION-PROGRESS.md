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

The cancellation tests also read the official party balance (`PartyBalanceReadService.GetContractBalancesAsync`, the engine behind statements, contract closing and reports): the customer receivable (600 × rows) and the service-provider payable (rows) return to zero after cancellation.

### Full suite (isolated build, heavy benchmarks excluded)

`dotnet test … --filter "Category!=Performance&Category!=PreviewSeed" --blame-hang-timeout 20m`, TRX `.artifacts/mashal/results/full-suite.trx`: 4,076 tests, 4,074 passed, 2 failed, 11.6 min. Both failures came from the shared `AccountingPostgreSql` collection database, not from product code:

- `DashboardServicePostgresTests`: its LINQ reference (last changed 2026-08-03) still counted cancelled receipts as receipts, while the dashboard has excluded them since 2026-09-23; it failed whenever another test in the shared database had cancelled a receipt. The reference now applies the dashboard's rules (cancelled receipt ≠ receipt; an active customer payment application counts as payment).
- `ReconciliationSummaryQueryCountTests`: 42 round-trips alone, 60 in the collection. A command-by-command comparison showed 18 statements that only run when their category has rows (direct loading sales, loading expense lines, transport legs, sarraf documents) — 50 distinct statements, none repeated per row, so no N+1. The ceiling is now the measured 60 with that evidence.

After these changes the whole collection passes: 354/354.

### Data-integrity finding: editing a posted expense

Reproduced with a test before fixing: with accounting enabled, editing the amount of an expense whose journal was posted (300 → 450) was accepted, updated the operational ledger to 450 and left the posted journal at 300. Expense accounting has only "created" and "reversed" events, and the edit path called neither. The fix follows the existing sale rule (`GetQuantityEditBlockerAsync`): when the expense's journal is posted, only the description (and technical stamps) may change; any other stored change is rejected with a message to cancel and enter a replacement. Description-only edits remain allowed (separate test). With accounting off (production today) no journal exists and behavior is unchanged.

### Local preview and browser check

Disposable database `ptg_oil_accounting_test_mashal_preview`, seeded through the real controllers with accounting enabled (24 loadings with posted purchases, a 6-row group sale, a 2-row group sale then cancelled, a 10-row group freight expense, a 4-loading bulk receipt). The app ran from the isolated build on `http://127.0.0.1:5001` (Development, auto sign-in off, real login). The user's own instance on port 5000 was not touched.

- `tests/browser/integrated-journey-responsive.cjs`: journey at 1440/1024/768/390 RTL with no horizontal overflow, 3 navigation groups, 7 tabs, stable avatars, keyboard focus kept; Contracts, Loading, group sale and group expense forms at 390 without overflow; no script errors; no business writes.
- Group pages read back: GSALE-1 active 360 t / 230,400 USD (6 × 60 t × 640); GSALE-2 cancelled with active 0 and its registered 120 t kept as history; GEXP-1 7,500 USD (10 × 60 t × 12.5); contract summary loaded 1,440 t, remaining 560 t, sold 360 t, expenses 7,500, 5.21 per t, purchase payable 720,000 — all match hand calculation. All pages 200, no overflow at 1440/390, no script errors.

### Branch re-check

`git fetch` shows the same heads as above. `git cherry`: integrity, backfill, expenses-finance, receipt-concurrency, ux, qa and main have nothing missing. `sales-sources` shows two "+" commits only because of conflict resolution; `git range-diff` confirms their content is present (`LossMode = ImmediateKnownLoss` already came from `76a8daf`; the `SupportedActions` change is parenthesization only).

## Open items / exact resume point

Done in the resume session: items 1–3 of the previous list (full suite with TRX, 1,000-row measurement and fix, local preview and browser check).

1. Heavy benchmarks run separately: `--filter "FullyQualifiedName~DatabaseOperationPerformanceTests"` (1,000 rows by default; `PTG_PERF_ROWS` scales a local run). Remaining `Category=Performance` classes (`ScaleAndPerformanceTests`, `PurchaseAccountingBatchPerformanceTests`, `LoadingWorkbookParserPerformanceTests`) were not part of this session's runs.
2. Not decided here (business decisions): whether a posted expense should support an in-place financial correction (reverse + repost) instead of cancel-and-replace; the GSALE status row shows "نیاز به بررسی" for a fully cancelled batch.
3. Known baseline limitation (unchanged): 18 `PartnerSettlementImport` cases need the external `Payment.xlsx` fixture, which is not in the repository.
4. Uncommitted user UI edits (`Views/*/CreateGroup.cshtml`, CSS 11/45/50/64) and the backup files remain uncommitted; the preview build included them.
5. Nothing merged into `main`, nothing pushed or deployed, no customer database used.
