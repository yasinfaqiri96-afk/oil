# Transport price estimate synchronization

The upstream `TransportLegPurchaseCostSync` originally rewrote every matching single-loading leg, including received, sold, cancelled and archived history. Reports prefer `PurchaseUnitCostUsd`, so an ordinary loading price edit changed historical transport/shipment profit without revising its financial documents.

Automatic synchronization is now limited to an active, unarchived, unconsumed and unposted leg in `Loaded` or `InTransit` status, sourced solely from the same active loading and purchase contract. Active receipts (including partial receipts), downstream transport allocations, sales source allocations, losses, an outbound inventory movement or a posted leg journal prevent synchronization. A manually overridden cost and a multi-loading source remain unchanged. Clearing a loading price does not erase a leg's existing cost.

The existing read-only operational lock and fiscal calendar services check both source loading and leg business dates. Fiscal checks cover the source contract company and the configured system owner. An installation without a fiscal calendar retains the existing accounting-disabled operational behavior. Configured missing/closed/locked periods are not treated as open.

No historical cost, journal, movement, allocation, price or rate is backfilled or deleted. Received or otherwise consumed legs need an explicit reviewed revision rather than automatic synchronization. Unpriced, still-unconsumed open estimates can be finalized to a positive price. The service does not save or post accounting; the existing caller owns persistence.

Regression coverage extends the upstream synchronization tests with completed/cancelled/archived/partially received/sold/continued/loss/posted-journal/fiscal and operational lock/price-clear guards. `TransportLegPurchaseCostSyncPostgresTests` verifies the real PostgreSQL query and persistence effects for open, partial receipt, closed fiscal year, locked period and posted journal scenarios. Actual pass/fail evidence belongs to the combined mission test artifacts; this document does not imply execution before those results exist.
