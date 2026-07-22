---
name: ptg-database-migration-safety
description: Use whenever PTG work mentions Entity, DbContext, EF Core model changes, PostgreSQL schema, table, column, relation, index, constraint, migration, pending model changes, data backfill, database restore, or production schema compatibility. Use it before creating, editing, applying, or reviewing a migration.
effort: high
---

# PTG Database and Migration Safety

Protect production data and keep EF Core history aligned with PostgreSQL.

## Authorization gate

- Do not change Entity, DbContext, migrations, schema, or data unless the user explicitly requested that scope.
- Do not apply a migration or modify a live database merely because a migration file exists.
- Never replace, truncate, or backfill production data without explicit approval and a verified backup.

## Before editing

1. Inspect `git status`, existing migrations, the model snapshot, entity configuration, and affected queries.
2. Explain the schema delta and why a UI/controller-only alternative is insufficient.
3. Check existing rows: nullability, defaults, uniqueness, foreign keys, delete behavior, decimal precision, timestamps, company/fiscal-year ownership, and old-record compatibility.
4. Plan safe expand/backfill/contract steps for destructive or required-column changes.
5. Keep connection strings, dumps, passwords, and keys out of source and output.

## Migration rules

- Generate the smallest migration matching the intended model change.
- Review generated SQL; do not trust scaffolding blindly.
- Avoid data loss, table rewrites, unbounded locks, cascade surprises, and silent precision changes.
- Add an index only for a demonstrated lookup, join, or order need and consider write cost.
- Make data migrations deterministic, restartable where possible, and safe for existing records.
- Preserve provider/version compatibility for PostgreSQL backups and restores.

## Verification

When the model truly changed, run the solution build, relevant tests, and EF pending-model check required by project rules. Inspect migration SQL and, when a disposable database is available, test apply and recovery. Never claim a live migration succeeded without checking the actual database state.

## Final report

State the schema change, data risk, migration created/applied status, backup status, commands, and rollback path.
