# MASHAL implementation checkpoint

## Workspace and authorization

- Reference audit: `/workspace/system-audit/system-scenarios-fa.html` (and its Markdown source), based on `b7cea8a4de2d1589c79f61b9055e83eb5d9c998b`.
- Remote main was based on that commit at the original baseline; after recovery it advanced to `df96f9a`. Repair branch starts at `73344bef35a7103b8e717b9849ec5cf345e0e3da`, which contains only the already requested avatar changes (PR 2).
- Original `/workspace/oil` remains untouched with four staged avatar files. Binary index/working patches, manifest and repository bundle saved outside the repository at `/workspace/mashal-baseline-backup`.
- Implementation worktree: `/workspace/mashal-repair`, branch `fix/mashal-integrity-workflows`. Specialist worktrees are separate; integration occurs by reviewed commits.
- Authorized scope includes the financial and inventory corrections requested in the mission. No production connection, production backfill, customer data reset, deployment or historical rate rewrite is authorized or performed.

## Baseline and issue classification

Full baseline completed on the unmodified repair base: 3947 executed, 3921 passed, 26 failed, 0 skipped (8m41s). Failures: 18 missing external Payment.xlsx cases, five stale localized expectations and three stale view contracts. Artifacts are under `/workspace/mashal-artifacts/baseline`; none have been rewritten.

| Finding | Current evidence | State before repair |
| --- | --- | --- |
| A01 bulk loading receipt accounting | `Create` invokes `TryPostInventoryReceiptAsync`; `BulkCreate` does not; movement writer has no accounting hook | Confirmed |
| Backfill cancellation | Historical loading/receipt enumeration and corresponding purchase adapter lack canceled guards | Confirmed |
| Backfill dry run | Missing source journal alone cannot prove all posting preconditions | Confirmed |
| A04 loading receipts | Single and bulk lock loading rows and re-read receipt/loss/transport commitments; bulk lacks FormToken stamp | Lock already present; replay guard missing |
| A04 transport receipts | Validation before transaction; receipt application has no protected re-read | Confirmed gap; PostgreSQL reproduction required |
| A05 decimal allocation | Remainder ranking is unchanged after assignment; same row repeatedly selected | Confirmed |
| A02 group sales/expenses | LoadingRegister absent from source loader and write switch | Confirmed |
| A03 conversion | Existing Serializable chunks, ordered loading locks and partial-success fallback | Already present; retry/resume requires repair |
| A06 stock/reservations/transport | Distinct readers and definitions already exist | Preserve; not a defect by itself |
| A07 source lineage | Allocation tables and continued-dispatch filtering already exist | Preserve; not removed |
| A08 docs | Platts/manual price and USD ledger documentation differs from current code | Confirmed stale documentation |
| A09 FX precision | FxRateMath uses 12 decimals; some supplier allocation storage/math uses 6 | Confirmed difference; financial impact under test |
| A10 shipment tests | Three expected English strings, controller returns appropriate Dari errors | Confirmed stale assertion; behavior separately verified |
| Uniform profit number | Realized sales profit, contract-cycle economics and shipment profit have different definitions | Rejected as a universal unification requirement |
| Universal receipt ceiling | Transport weighbridge surplus can be valid when represented by negative shortage | Rejected as a universal rule; net consumption and concurrency still guarded |

## Operation ownership and dependency map

| Operation | Command owner | Quantity/physical effects | Financial effects | Lineage |
| --- | --- | --- | --- | --- |
| Loading / Excel | LoadingController + existing loading helpers | Contract capacity; no stock receipt | SupplierLoadingLedger + PurchaseAccountingAdapter | LoadingRegister -> purchase contract |
| Single/bulk loading receipt | LoadingReceiptsController + InventoryMovementWriter + LossEventWorkflowService | Active receipt + shortage + loading-to-leg allocations consume source; inbound movement only for inventory destination | PurchaseAccountingAdapter -> posting service -> inventory valuation | Receipt -> loading; allocation -> source contract/movement/sale/dispatch |
| Loading-to-transport | TransportWorkflowService | Source allocation; no fictitious tank movement | Initial purchase remains owned by original loading | Leg allocation SourceLoadingRegisterId |
| Inventory-to-transport | InventoryTransportBatchService | Real stock Out once at origin | Transfer valuation/accounting at arrival | Movement/source contract allocation |
| Transport receipt/continuation | InventoryTransportReceiptService / TransportChainService | Receipt consumes remaining; destination inbound or direct sale or child leg | Purchase/transfer/sales adapters; freight/shortage services | Parent/child, SourceLoadingReceiptId and all contract shares |
| Single/group sale | SalesController primitives / receipt service | Stock Out only for stock; direct sources consume their own remaining | Customer ledger + SalesAccountingAdapter + COGS | SalesTransactionSourceAllocation and canonical receipt allocation |
| Loading/group expense | Existing loading expense helpers / ExpensesController | No cargo movement | ExpenseLedgerPoster + ExpenseAccountingAdapter; settlement mode and freight responsibility | Loading/leg/dispatch + contract allocation |
| Cancellation/correction | Lifecycle/cancellation services | Original retained; reversal and dependency checks | Reversal journals/ledger; historical values retained | Original source and revision/reversal links |
| Backfill | AccountingBackfillService -> existing adapters | No new operational receipt or stock movement | Only missing eligible journal; canceled/reversed/closed/uncertain sources distinguished | Existing SourceEventId per document |
| Journey/reporting | Read-only query services | No writes | Distinct historical/as-of/cycle definitions | Compatibility-dispatch deduplication retained |

## Phase sequence

1. Foundation: preserve changes, full baseline, confirmed findings and owners.
2. Critical: A01, backfill safety, A04 concurrency/replay, A05 fair exact allocation; focused real PostgreSQL tests.
3. Workflows: shared source eligibility, direct loading group sales/expenses, durable partial conversion retry.
4. Financial: only demonstrated COGS, settlement and FX issues; retain historical definitions and snapshots.
5. UX: current design tokens, clear Dari grouping/navigation, preserved routes/features and responsive checks.
6. Regression/performance: full suite, meaningful database assertions and bounded measured benchmarks; no claims from unmeasured routes.
7. Documentation/review: actual test counts, migrations/rollback instructions, outstanding limitations and deployment readiness.

## Current checkpoint

Recovery completed and all seven repair/specialist branches were pushed safely without merging main. Latest critical build: zero errors, 18 warnings. Revalidated focused run: **147 passed, zero failed, zero skipped**, 52 seconds, including real PostgreSQL receipt/accounting/backfill/concurrency cases. Original failed run remains as evidence; allocator deferred floors and receipt xmin refresh defects were fixed before this passing execution.

Evidence: `/workspace/mashal-artifacts/phase2/critical-revalidated.trx`, `.log` and `critical-revalidated-summary.json`. Structural UI cases matching the broad receipt-name filter are tracked in the later UI phase rather than this operational run.

Next: review/integrate prepared transport retry, sales/shared sources, expense/financial and UX commits; validate them together with the newer remote-main changes on the review branch. No production access, historical backfill or deployment occurred. Updated remote main is `df96f9a`; its automatic transport-cost synchronization requires an additional history-safety guard before incorporation.
