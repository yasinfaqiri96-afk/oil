---
name: ptg-stock-inventory-guard
description: Use whenever PTG work touches StockService, IStockService, InventoryMovement, inventory balance, receipts, loading receipts, dispatch, sales, DirectSale, DirectDispatchFromReceipt, allocation, transport legs, tank stock, shortage, loss, reversal, or quantity posting. Use it before changing any stock-producing or stock-consuming flow.
effort: high
---

# PTG Stock and Inventory Guard

Protect physical-stock truth and traceability. A UI simplification must never invent, duplicate, or bypass a stock event.

## Investigation

1. Check `git status` and preserve existing work.
2. If `graphify-out/graph.json` exists, query the named flow for orientation.
3. Read the exact controller, service, entity, and related tests; source and tests remain authoritative.
4. Describe the current quantity flow before editing: origin, destination, movement type, allocation/trace, and posting time.
5. Identify create, edit, delete, reversal, retry, and partial-failure behavior.

## Invariants

- Treat `StockService` and `InventoryMovement` as the official stock mechanism.
- A receipt whose destination is `ToInventory` must create the correct inbound movement exactly once.
- A `DirectSale` must not create a fake inventory movement.
- `DirectDispatchFromReceipt` must remain allocation/trace based and must not call `StockService` as if inventory were received first.
- Never create a synthetic movement merely to simplify UI or reporting.
- Preserve decimal quantities, units, conversion direction, company, product, terminal/tank, and effective date.
- Prevent duplicate posting on retries and insufficient/negative stock unless the existing business rule explicitly permits it.
- Keep stock mutation and its required trace/accounting writes transactionally consistent.

If the flow also posts money or LedgerEntry records, use `ptg-finance-ledger-guard` as well.

## Change boundary

- Prefer a small fix in the existing flow.
- Do not change entities, DbContext, migrations, allocation rules, or business calculations without explicit user approval.
- Do not redesign unrelated controllers or normalize old data silently.

## Verification

Build the Web project first, then run only tests covering the affected receipt, sale, dispatch, transport, or stock flow. Include duplicate-submit and edit/reversal coverage when relevant. Run broader tests only when a shared stock service or model changed.

## Final report

State the quantity impact, movement created or intentionally not created, trace/allocation impact, changed files, and exact verification performed.
