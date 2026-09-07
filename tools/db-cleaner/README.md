# DB Cleaner

Small helper to back up and truncate operational application data tables in the `public` schema while preserving authentication and master/reference data.

Usage (PowerShell):

```powershell
dotnet run --project tools/db-cleaner/DbCleaner.csproj
```

The tool preserves `Users`, `Roles`, `__EFMigrationsHistory`, and master/reference data: `Products`, `Currencies`, `Units`, `Partners`, `Companies`, `Suppliers`, `Customers`, `ServiceProviders`, `Terminals`, `StorageTanks`, `Vessels`, `Trucks`, `Wagons`, `Drivers`, `Locations`, `ExpenseTypes`, and `Employees`.

The tool reads connection string from `DATABASE_URL` or `ConnectionStrings__DefaultConnection` environment variables, falling back to `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=ptg_oil_system;`.

Backups will be written to `artifacts/db-backup-<timestamp>/`.
