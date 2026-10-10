# Accounting backfill safety (MASHAL)

## Confirmed defects and changes

The original backfill selected cancelled `LoadingRegister` and `LoadingReceipt` rows. Their purchase and loading-receipt adapters had no cancellation guard; the batch purchase fast path also bypassed cancellation. A dry run counted every missing source journal as `Posted` and the command printed `to post`, despite not checking mappings, price, accounting periods or preceding stages.

Backfill now reports each source document as `Posted`, `AlreadyPosted`, `Skipped`, `Cancelled`, `NeedsReview` or `Error`, with an explicit reason. `MissingJournal` is a separate fact, not a claim of eligibility. Dry run never invokes the chart seeder or a posting adapter and its `Posted` count is always zero. Documents missing journals need adapter validation before eligibility can be established.

Cancelled purchases and receipts cannot be posted by single, batch or backfill paths. Real PostgreSQL replay refreshes and locks source rows inside each document transaction. Journal and valuation changes remain in that transaction; cancellation during candidate discovery is rechecked before posting. Cancellation tokens propagate rather than becoming ordinary per-document errors.

Backfill only fills missing accounting events. It does not reprice a previously posted purchase: all purchase revisions are recognized, and an already reversed source is skipped. The explicit repricing adapter workflow still reverses the previous active revision and creates the next revision. A reversed latest revision cannot be revived through another reprice call. A receipt must have exactly one unreversed posted purchase, and its effective purchase amount must match that journal; an edited price without a posted revision produces `PURCHASE_PRICE_CHANGED_NEEDS_REVIEW`.

Loading receipts destined for direct dispatch are not inventory receipts and do not populate an inventory valuation pool. Historical `Mixed` receipts are explicitly skipped by this scalar inventory adapter: valuing their entire header quantity as inventory would include direct-sale/direct-dispatch shares. They require allocation-level review; no valid operational allocation is deleted or changed.

## Verification

`AccountingBackfillSafetyTests` uses the repository's real PostgreSQL temporary-database fixture. Cases cover single and fast batch cancelled purchases, cancelled/direct-dispatch/mixed receipt valuation, read-only dry run classification, cancelled replay, historical price changes, posted revisions, reversal dependencies, pending prices and hard-locked periods. Existing `PurchaseAccountingAdapterTests` retain the explicit repricing, duplicate and inventory valuation regression checks. Run together with `TransportReceiptAccountingTests`.

No migration was added. No operational document, historic journal, price, exchange rate or allocation is rewritten by these changes. No production database or backfill command was used during development.

## Remaining limits

Dry run intentionally does not promise a complete posting plan. It cannot simulate the dependent valuation pool or closed-period results without executing adapters. Existing purchase revisions are not compared to today's edited price during backfill; changes remain a separately authorized revision operation. Historical mixed receipt valuation requires explicit allocation-aware accounting review before any replay. Historical backfill COGS chronology and valuation estimates still require the broader reconciliation review; this safety patch does not assert historical financial correctness for every dataset.

## Scope of the regression matrix

| Safety scenario | Automated coverage in this patch |
| --- | --- |
| Cancelled purchase through single and batch adapter | Yes; source journals are absent |
| Cancelled loading receipt | Yes; source journal and valuation are absent |
| Direct dispatch / historical mixed loading receipt | Yes; both are skipped without valuation |
| Cancelled transport receipt | Yes; adapter and real replay remain inactive |
| Missing / posted / cancelled dry-run documents | Yes; zero posting and unchanged journal / pool counts |
| Historical price edited without journal revision | Yes; original journals are preserved and receipt needs review |
| Valid explicit purchase revision | Existing repricing regression plus new backfill preservation case |
| Reversed purchase and reversed loading receipt | Yes; reversal history and valuation remain unchanged |
| Pending price, hard-locked period and closed fiscal year | Yes; precise reasons and no new source journal |
| Concurrent manual journal reversal versus a dependent receipt | Not covered or resolved by this patch |
| Historical allocation-aware mixed receipt valuation | Not replayed automatically; requires review |
| Complete historical COGS / foreign-exchange reconciliation | Broader financial review; not established by this patch |

The matrix describes implemented tests, not an assertion that all tests passed. The combined mission test artifacts provide the actual execution results. `AccountingPostingService.ReverseAsync` does not itself validate every downstream operational dependency; a concurrent manual journal reversal needs separate dependency and authorization review. Backfill's conservative journal scan prevents replay of a source found already posted or reversed, but cannot by itself make every manual reversal safe.
