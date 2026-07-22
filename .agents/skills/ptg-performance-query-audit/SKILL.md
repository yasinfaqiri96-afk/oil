---
name: ptg-performance-query-audit
description: Use whenever a PTG page, report, dashboard, search, export, controller, EF Core query, PostgreSQL query, build, or test is described as slow, heavy, timing out, using too much memory, causing N+1 queries, loading too many rows, or needing optimization. Measure before changing behavior.
effort: high
---

# PTG Performance and Query Audit

Keep only measured improvements that preserve business results.

## Measure first

1. Preserve the working tree and define the exact slow action, dataset size, environment, and success metric.
2. Capture a repeatable baseline: elapsed time, query count or SQL, rows, allocations/memory, build/test duration, or browser timings.
3. Identify the dominant cost before editing; do not optimize by intuition alone.

## Safe optimization order

1. Remove accidental repeated work and N+1 queries.
2. Use projection and `AsNoTracking` for genuinely read-only queries.
3. Filter, sort, aggregate, and paginate in the database instead of loading unbounded rows.
4. Remove unnecessary `Include` graphs while preserving required semantics.
5. Inspect generated SQL and index use; request approval before an index migration.
6. Cache only stable, non-sensitive lookups with a clear lifetime and invalidation rule.

Do not change money, FX, stock, allocation, P&L, permission, or report semantics to gain speed. Do not hide slow work behind stale data.

## Verification

Run the same benchmark before and after, repeat enough times to avoid one-off noise, and run targeted correctness tests. Revert experiments that do not produce a clear win. For UI performance, validate the real page and network behavior when possible.

## Final report

Report baseline, result, percentage change, query/behavior change, correctness checks, and any environment limitation. Never claim an improvement without measurements.
