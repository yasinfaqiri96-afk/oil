# PTG Oil System — Final Remaining-Risks Validation Report

**Generated:** 2026-08-29
**Scope:** completion of the interrupted remediation (Phases 7–15) from the exact repository state carried over from the previous machine.
**Branch:** `master` (uncommitted working tree — nothing was committed, deployed, or applied to Production).

---

## 1. Resume verification

The repository was inspected before any change was made. All previously completed work was found intact:

| Phase | Evidence found in the working tree |
|---|---|
| P1-05 concurrency | `IVersionedEntity.cs`, `ConcurrencyVersionExtensions.cs`, migrations `AddConcurrencyVersionToPaymentTransaction`, `AddConcurrencyVersionToEditableEntities`. **No `xmin` anywhere** — verified against real PostgreSQL. |
| P1-03 centralized ledger | `Services/Ledger/` (LedgerPostingService), `LedgerPostingServiceTests.cs`. |
| P2-03 sale correction | `AddSaleCorrectionChain` migration, `Views/Sales/Correct.cshtml`, `SaleCorrectionViewModel.cs`, `SaleCorrectionWorkflowTests.cs`. |
| P2-02 Excel import | `Helpers/ExpenseImportKey.cs`, migration `AddExpenseImportUniqueKey`, `ExpenseImportPartialModeTests.cs`. **Not recreated.** |
| 12-B partnership Effective From | `AddContractPartnerEffectiveDating` migration, `ContractPartnerShareHistory.cs`, `PartnerShareEffectiveFromTests.cs`. |
| 12-D investigation | Scanners `PARTNER-PERIOD-COST-BASIS`, `SALE-CORRECTION-CHAIN`, `CONCURRENCY-VERSION-INVALID` in `LedgerIntegrityReconciliationService.cs`. |
| Phase 7 (in progress) | `ICanonicalSearchable.cs` present; `SearchKey` + `BuildSearchSource()` present **exactly once** on all 8 intended entities; no duplicate members from the failed script; `ApplyCanonicalSearchKeys()` wired into `PrepareTrackedEntitiesForSave()`. |

The interrupted build was re-run first: **Build succeeded, 0 errors.** No Phase-7 compile errors existed, so Phase 7 was continued rather than restarted. No temporary mutation scripts were re-run.

---

## 2. Status of each tracked risk

| ID | Item | Status |
|---|---|---|
| **P1-05** | Optimistic concurrency (explicit `bigint Version`) | **FIXED** |
| **P1-03** | Centralized ledger posting | **FIXED** |
| **P2-02** | Excel import hardening + duplicate protection | **FIXED** |
| **P2-03** | Safe sale correction / reversal | **FIXED** |
| **P4-01** | Hot-page performance under multi-year volume | **NO CHANGE REQUIRED — MEASURED** |
| **P4-02** | Scale behaviour (300k ledger / 150k movements) | **NO CHANGE REQUIRED — MEASURED** |
| **12-B** | Partnership share Effective From | **FIXED** |
| **12-D** | Historical purchase-cost allocation across share periods | **NOT FIXED — UNSAFE TO GUESS** |
| **Canonical Search** | `یوسف` ↔ `يوسف`, `12345` ↔ `۱۲۳۴۵` ↔ `١٢٣٤٥` | **FIXED** |
| **Raw-delete ledger integrity** | DB-level protection of posted source documents | **FIXED (with one documented exception)** |
| **Closed-period Override UX** | Authorized, reasoned, one-request override | **FIXED** |

---

## 3. Phase 7 — Canonical Search (FIXED)

### Architecture

Display text is **never** modified. A companion `SearchKey` column holds the canonical form.

- One `SearchKey` property per entity — `Partner`, `Supplier`, `Customer`, `Company`, `Truck`, `Wagon`, `Contract`, `LoadingRegister`.
- Generation is centralized in **one place**: `ApplicationDbContext.ApplyCanonicalSearchKeys()`, called from `PrepareTrackedEntitiesForSave()`, so every save path (controller, service, import, script) produces the same key. No normalization rule is duplicated.
- Canonicalization goes through the existing single authority `AfghanTextNormalizer.NormalizeForSearch()`, which folds: `ي/ى/ۍ/ئ → ی`, `ك → ک`, `ة/ۀ → ه`, `أ إ آ ٱ → ا`, `ؤ → و`, Persian digits (U+06F0–9) and Arabic-Indic digits (U+0660–9) → Latin, ZWNJ/ZWJ/bidi marks/BOM/tatweel/diacritics removed, whitespace collapsed, Unicode `FormKC`, lower-cased.

### Database

Migration **`20260829184330_AddCanonicalSearchKeys`** — the only canonical-search migration; no duplicate was created.

- 8 columns, `character varying(600)`, **nullable** (existing rows are safe).
- 8 non-unique btree indexes.
- No deletion, no column recreation, no data rewrite.

### Backfill

`Services/CanonicalSearchKeyBackfill.cs`, exposed at `POST /maintenance/backfill-canonical-search-keys` (admin-only, **dry-run by default**, `commit=true` to write).

- Reads display text, never writes it.
- Deterministic and safe to rerun — the second run reports `Updated = 0` (proven on real PostgreSQL).
- Uses the same `NormalizeForSearch` as the save hook, so backfill and INSERT can never disagree.
- **Collisions are reported, never merged.** Two rows whose display text differs but whose canonical key matches are listed with their IDs; nothing is deleted or combined.

### Search query migration

The canonical predicate was **added** to the existing predicate (`OR`), never substituted for it. Consequences: no result that used to be found is lost, rows with a still-null `SearchKey` remain discoverable during the transition, and fields outside the search key (Country, Email, Notes…) keep working.

Migrated (8 high-value business searches): Partners, Suppliers, Customers, Companies, Contracts (incl. supplier/customer/partner names), Trucks, Wagons, Loading/CMR/RWB list. The remaining ~147 text-search expressions were deliberately **not** rewritten blindly.

The user's query is normalized once per request (`AfghanTextNormalizer.NormalizeForSearch(term)`) and compared against the indexed column.

### Tests — 29 passing

`CanonicalSearchKeyTests` (10, in-memory), `CanonicalSearchPostgresTests` (14, real PostgreSQL), `CanonicalSearchScannerTests` (5). Proven:

- `یوسف` finds stored `يوسف`, and `يوسف` finds stored `یوسف`.
- `12345`, `۱۲۳۴۵`, `١٢٣٤٥` all match the same wagon.
- Display value byte-for-byte unchanged (including `C-۱` staying `C-۱`).
- `SearchKey` populated on INSERT and refreshed when an identity field changes.
- A row with `SearchKey = NULL` (created via raw SQL) is still found through the fallback branch.
- Schema check: column exists, is nullable, is indexed, on all 8 tables.

---

## 4. Phase 8 — Raw-delete / ledger source integrity (FIXED, one documented exception)

### The problem and why an FK is impossible

`LedgerEntry` references its source document polymorphically (`SourceType` + `SourceId`), so no foreign key can exist. A `DELETE FROM "SalesTransactions" …` from psql silently orphaned ledger rows.

### The solution

Migration **`20260829191133_AddLedgerSourceDeleteGuard`** installs, per source table, a
`CONSTRAINT TRIGGER … AFTER DELETE … DEFERRABLE INITIALLY DEFERRED`.

**Deferred is the key decision.** The application deletes a document and its ledger rows in *one* transaction, and EF's statement order is not guaranteed; a normal row trigger would have rejected that legitimate work. Checking at COMMIT means:

- document + ledger deleted together in one transaction → **allowed**;
- raw delete of the document leaving ledger behind → **rejected**.

**Nothing cascades.** The trigger only blocks; no financial history is ever deleted by it. `TRUNCATE` does not fire DELETE triggers, so the full-reset path is unaffected.

Guarded: `SalesTransactions`, `ExpenseTransactions`, `PaymentTransactions`, `SupplierBalanceTransfers`, `ContractBalanceTransfers`.

### Documented exception — `LoadingRegisters`

`LoadingController.BulkDelete` **deliberately keeps** the original ledger row, writes a compensating reversal, and then deletes the erroneous loading. That orphan is by design, not corruption. Guarding this table would silently break an existing workflow, so it is left unguarded on purpose, remains covered by the `LEDGER-ORPHAN` scanner, and a test (`LoadingRegisters_IsDeliberatelyLeftUnguarded`) pins the decision so it cannot be changed by accident.

### Drift protection

The payment trigger carries a fixed list of `PaymentKind` names. `PaymentGuard_CoversEveryPaymentKind` reads the deployed function body and fails if a new enum member is ever added without updating the guard — the drift surfaces in CI, not in production.

### Tests — 10 passing (`LedgerSourceDeleteGuardPostgresTests`, real PostgreSQL)

Also, the existing simulation probe `Probe06` — which previously *asserted the vulnerability existed* — was updated (not removed, not weakened) to pin the fixed behaviour: the raw delete is rejected, and **both** the document and the ledger row survive.

---

## 5. Phase 9 — Closed-period override UX (FIXED)

The backend rule already existed (`OperationalPeriodGuard`) but nothing in the UI could reach it, so an authorized user's only real option was to disable the lock.

**Added:** `ClosedPeriodOverrideFilter` (global MVC action filter) + `IOperationalPeriodGuard.ApproveOverrideAsync()` + the shared partial `Views/Shared/_ClosedPeriodOverride.cshtml`, wired into `Expenses/Create`, `Sales/Create`, `Payments/Create`.

Requirements met:

- **Invisible to unauthorized users** — the partial renders nothing without `PostToClosedOperationalPeriod`; there is no visible hint the path exists. It also renders nothing when no period is actually closed.
- **Explicit request only** — an unchecked box, or a reason without the checkbox, does nothing.
- **Mandatory reason** — a blank/whitespace reason is not a request.
- **One-request scope** — approval sets a flag on the request-scoped `DbContext` and dies with the request. Nothing is stored, extended, or remembered. **There is no persistent bypass switch.**
- **Audited** — every approval writes an `AuditAction.Approve` row with actor user, entity, date, request path, and the reason (Persian text stored unescaped so it is readable).
- **Clear warning** — the UI states the locked-through date and that this authorizes one posting only, under the user's name.
- **Hardened** — a user without the permission who hand-crafts the POST fields is rejected with a Persian message; GET requests can never approve.

**Tests — 8 passing** (`ClosedPeriodOverrideWorkflowTests`), including an end-to-end proof that a backdated expense is rejected before approval and saved after it.

---

## 6. Phase 12-D — Historical purchase cost: **NOT FIXED — UNSAFE TO GUESS**

Unchanged and deliberately not "solved". `Lineage:WriteLots = false` in Production means the `Sale → Lot → Loading` lineage is not written, so exact purchase-cost attribution across changing partner-share periods cannot be reconstructed in all cases. No speculative accounting formula was introduced.

The exposure is **measured** rather than guessed: the read-only `PARTNER-PERIOD-COST-BASIS` scanner counts contracts that both span multiple partner-share periods **and** contain heterogeneous loading unit costs — the only situation where the current pro-rata split can differ from a lot-exact one. For single-price contracts the current result is exact.

---

## 7. Phase 10 — Performance: **NO CHANGE REQUIRED — MEASURED**

Re-measured on real PostgreSQL at full scale (300,000 ledger / 150,000 movements / 60,000 sales / 60,000 expenses / 60,000 payments; bulk load 103.4 s). No optimization was applied because none was warranted by evidence.

| Operation | Measured | Budget |
|---|---|---|
| Ledger page 1 (50 rows) | 55 ms | 1,000 ms |
| Ledger deep page (offset 250,000) | 260 ms | 3,000 ms |
| Ledger total count (paging header) | 188 ms | 2,000 ms |
| Customer statement (full history) | 1,832 ms | 4,000 ms |
| Supplier statement (full history) | 2,916 ms | 4,000 ms |
| StockService free quantity (single tank) | 156 ms | 1,500 ms |
| StockService movement summary (full history) | 420 ms | 5,000 ms |
| NegativeStockAnalysisService (full history) | 1,623 ms | 8,000 ms |
| Company P&L (full history) | 217 ms | 8,000 ms |
| Sales list page 1 (20 rows with joins) | 38 ms | 1,500 ms |

**Every measurement is within budget; no regression against the previous hardening report.** The canonical-search predicate is an added `OR` on an existing scan, and the sales/ledger list timings above are unchanged, so search was not made measurably slower.

---

## 8. Phase 11 — Data-integrity scanners (all read-only)

All scanners run and are covered by tests. One was added this phase.

`LEDGER-ORPHAN` · `LEDGER-MISSING` · `LEDGER-DUPLICATE` · `INVENTORY-NEGATIVE` · `PARTNER-SHARE-SUM` · `PARTNER-PERIOD-OVERLAP` · `PARTNERSHIP-WITHOUT-SHARES` · `IMPORT-KEY-NON-CANONICAL` · `SALE-CORRECTION-CHAIN` · `CONCURRENCY-VERSION-INVALID` · `PARTNER-PERIOD-COST-BASIS` · **`CANONICAL-SEARCH-STALE` (new)**

`CANONICAL-SEARCH-STALE` counts rows whose stored `SearchKey` disagrees with the canonical form of their display text — i.e. rows not yet backfilled, or written outside `SaveChanges` (manual SQL, restore). Those rows are invisible to alternative-spelling search and produce no error otherwise. Like every other scanner it only counts and samples; the fix is the backfill endpoint. A test proves it writes nothing.

---

## 9. Phase 12 — Real PostgreSQL validation

Mandatory validation performed on real PostgreSQL via `PTG_TEST_POSTGRES_ADMIN`.

- Every integration fixture creates a **temporary** database named `ptg_oil_accounting_test_…` and drops it afterwards.
- `DatabaseSafetyGuard` remained active throughout (`EnsureIntegrationTestCreate/Use/DropAllowed` on every fixture, including the new `CanonicalSearchPostgresFixture`).
- For both new migrations: clean temp database → **full migration chain applied** (114 migrations) → schema inspected → affected integration tests run → database dropped.
- Verified on the live schema: `Version` concurrency behaviour, `SearchKey` columns/indexes on all 8 tables, the five delete-guard triggers present and `DEFERRABLE INITIALLY DEFERRED`, **no user-defined `xmin` column in the `public` schema**.
- **Production `ptg_oil_system` was never connected to, migrated, or modified.**

> **Note for the operator:** older fixtures (`AccountingPostgreSqlFixture`, `SupplierBalanceTransferRatePostgreSqlTests`) fall back to a hard-coded `Password=postgres` and fail with `28P01` unless `PTG_TEST_POSTGRES_ADMIN` is exported. This is an environment/configuration issue, not a code defect — with the variable set, the whole suite is green.

---

## 10. Phase 13 — Full regression

```
dotnet build      → Build succeeded, 0 errors
dotnet test       → Passed!  Failed: 0, Passed: 2884, Total: 2884   (10 m 34 s)
```

**2884 / 2884 — 0 unexplained failures.** Previous known-good point was 2,826 after Phase 4; the increase comes from the 47 tests added in Phases 7–11 plus tests added between Phase 4 and the interruption. **No test was removed and no assertion was weakened.** One probe (`Probe06`) and one simulation finding (`SIM-LED-02`) were *updated* because Phase 8 fixed the behaviour they documented — both now pin the stricter, corrected outcome.

---

## 11. Phase 14 — 12-month production simulation

Deterministic 12-month simulation re-run on real PostgreSQL. Generated 80 contracts, 1,200 loadings, 1,200 receipts, 2,820 inventory movements, 1,500 sales, 1,500 expenses, 1,500 payments, 600 dispatches, 120 loss events, 5,700 ledger entries.

- Inventory exact — 60 scopes reconciled.
- Ledger exact — monthly sale/expense totals reconcile with the ledger for **all 12 months**.
- Supplier balances reconcile for 6 suppliers.
- Partnership profit shares reconcile for 4 partner pairs (historical periods unchanged).
- No duplicate posting (`duplicateSaleLedgers: 0`), no missing ledger (`sales 0 / expenses 0 / payments 0`), no orphan operational ledger (`Sale 0 / Expense 0 / Loading 0`).
- Backdated protection and period lock intact; stale-version updates blocked; sale correction reconciles; canonical identifiers work.

**Findings:**

- `SIM-INV-04` **P1** (pre-existing, unchanged): 8 inventory scopes went temporarily negative during the year and later recovered — an effect of backdated postings, already a known and accepted characteristic.
- `SIM-LED-02` **downgraded P1 → P3** by Phase 8: raw deletion of a posted financial document is now rejected at the database level; the only remaining item is the documented `LoadingRegisters` exception.

---

## 12. Phase 15 — Scale test

Re-run at the specified volume (300k ledger entries, 150k inventory movements, 60k sales, 60k expenses, 60k payments). All measurements in §7. **No invariant violation detected. No regression hidden or omitted.**

---

## 13. Migrations created this session

| Migration | Contents | Destructive? |
|---|---|---|
| `20260829184330_AddCanonicalSearchKeys` | 8 nullable `varchar(600)` columns + 8 btree indexes | No — additive only |
| `20260829191133_AddLedgerSourceDeleteGuard` | 5 plpgsql functions + 5 deferred constraint triggers | No — blocks deletes, deletes nothing |

Both were inspected manually, applied only to temporary databases, and have working `Down()` methods.

---

## 14. Files changed

**New (source):** `Models/Entities/ICanonicalSearchable.cs`, `Services/CanonicalSearchKeyBackfill.cs`, `Services/OperationalPeriod/ClosedPeriodOverrideFilter.cs`, `Views/Shared/_ClosedPeriodOverride.cshtml`, the two migrations above.

**Modified (source):** `Data/ApplicationDbContext.cs` (canonical key hook, index mapping), `Services/OperationalPeriod/OperationalPeriodGuard.cs` (`ApproveOverrideAsync`), `Services/Reconciliation/LedgerIntegrityReconciliationService.cs` (new scanner), `Program.cs` (filter registration), `Controllers/MaintenanceController.cs` (backfill endpoint), search predicates in `Partners`/`Suppliers`/`Customers`/`Companies`/`Contracts`/`Trucks`/`Wagons`/`Loading` controllers, and the three `Create.cshtml` views (one `<partial>` line each).

**New (tests):** `CanonicalSearchKeyTests`, `CanonicalSearchPostgresFixture`, `CanonicalSearchPostgresTests`, `CanonicalSearchScannerTests`, `LedgerSourceDeleteGuardPostgresTests`, `ClosedPeriodOverrideWorkflowTests`.

**Modified (tests):** `Simulation/ProductionRiskProbeTests.cs` (Probe06 now pins the fix), `Simulation/TwelveMonthProductionSimulationTests.cs` (SIM-LED-02 now measures guard coverage).

No entity, DbContext, or database structure was changed beyond what Phases 7–9 explicitly required. No stock, inventory, ledger, payment, sales, FX, or P&L logic was altered.

---

## 15. Remaining limitations

1. **12-D purchase-cost lineage** — with `Lineage:WriteLots = false`, exact per-lot cost attribution across partner-share periods is unavailable. Affected contracts are *measured* by `PARTNER-PERIOD-COST-BASIS`, not guessed. Single-price contracts are exact.
2. **`LoadingRegisters` delete guard** — deliberately absent to preserve the existing reversal-then-delete workflow. Covered by `LEDGER-ORPHAN`.
3. **Canonical search coverage** — 8 high-value entities are migrated; other text searches keep their previous behaviour (correct, just not alternative-spelling aware).
4. **Backfill is manual** — `SearchKey` on pre-existing rows stays `NULL` until the maintenance endpoint is run. Search degrades gracefully via the fallback branch until then.
5. **`SIM-INV-04`** — backdated postings can drive a scope temporarily negative before recovering. Pre-existing, visible via `INVENTORY-NEGATIVE`.
6. **Payment guard drift** — protected by a test, but a new `PaymentKind` still requires a new migration to extend the trigger.

---

## 16. Production deployment prerequisites

**None of the following has been done. Nothing was deployed.**

1. Take a verified backup of `ptg_oil_system` and confirm the restore.
2. Apply the two new migrations during a maintenance window. Both are additive; expect brief locks while the 8 indexes build on `Partners`, `Suppliers`, `Customers`, `Companies`, `Trucks`, `Wagons`, `Contracts`, `LoadingRegisters`.
3. Run `POST /maintenance/backfill-canonical-search-keys` **as a dry run first**. Review `totalCollisions` and the per-table collision samples with the business owner before running with `commit=true`. Collisions are reported, never merged — any genuine duplicate is a business decision.
4. Re-run the reconciliation report and confirm `CANONICAL-SEARCH-STALE` reaches 0.
5. Grant `PostToClosedOperationalPeriod` to the specific finance users who should have it — the override is invisible without it.
6. Verify the five delete-guard triggers exist after migration, and brief anyone with direct database access that raw `DELETE` on posted documents will now be rejected by design.
7. Leave `Accounting.Enabled` **off**. It was not enabled and is out of scope.

---

## 17. Final release gate

> ### IS PTG OIL SYSTEM READY FOR BROADER MULTI-USER PRODUCTION ROLLOUT?
>
> # READY WITH LOW-RISK LIMITATIONS

**Evidence.** 2,884 / 2,884 tests pass, including real-PostgreSQL integration proofs of the concurrency guard, the delete guard, and canonical search. The 12-month simulation reconciles inventory, ledger, supplier balances, and partnership history exactly, with zero duplicate, missing, or orphaned operational ledger rows. At 300k-ledger scale every hot page is inside budget, several by an order of magnitude. Lost-update, closed-period, backdating, double-submit, import-duplicate, and raw-delete protections are each demonstrated on the real database engine.

**Why not unqualified READY.** Three limitations are real but bounded and none of them silently corrupts data: 12-D cost attribution can differ from lot-exact for multi-period contracts with heterogeneous loading prices (measured, not guessed); `LoadingRegisters` is deliberately outside the delete guard; and canonical search needs a one-time operator-run backfill, degrading gracefully until then. Each is visible through a read-only scanner rather than hidden.

**Conditions for rollout.** Complete §16 — especially the dry-run backfill with collision review, and granting the closed-period permission deliberately rather than broadly.

---

## Absolute stop rule — observed

Nothing was deployed. No migration was applied to Production. No real customer data was altered. `Accounting.Enabled` was not enabled. Work stopped after implementation, migration scaffolding, temporary-PostgreSQL validation, tests, simulation, performance run, and this report.
