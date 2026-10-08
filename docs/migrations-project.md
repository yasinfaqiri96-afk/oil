# Separate migration compilation

Dependency direction (all configurations):

```text
Web -> Persistence
Web -> Migrations -> Persistence
Tests -> Web (and the existing partner-settlement-import tool)
```

This follows [EF Core's separate migrations project pattern](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects).
Neither Persistence nor Migrations references Web. Persistence links the existing
DbContext, configurations, entity sources and their small supporting types from
their original Web paths. Namespaces remain `PTGOilSystem.Web.*`; Web excludes
these linked sources from its Compile items. No duplicate CLR entity types exist.
The lock exceptions/scope helpers were separated from Web service implementations
without changing their logic. The operational-period message keeps its original
public forwarding API and calendar-aware formatting.

All existing migration files and the snapshot now live in
`src/PTGOilSystem.Migrations/Migrations/`, with their original contents and
namespaces. The migration-owned SQL helper is linked from its existing Data path.
Npgsql uses `PTGOilSystem.Migrations` explicitly in startup and the design-time
factory. The context's provider-specific default also covers direct Npgsql
construction by tools/tests, preserving explicit overrides and other providers.
The history table name/schema and migration IDs are unchanged.

After switching to this structure, restore the changed project graph **once**:

```powershell
dotnet restore tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-restore
dotnet ef migrations list --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-build
dotnet ef migrations has-pending-model-changes --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-build
```

Use the configured connection environment and existing database safety rules.
The two EF checks above do not apply migrations. To scaffold an **authorized**
future model change, target the migrations project, use Web as startup project,
and pass `--output-dir Migrations`; do not regenerate historical migrations.
Authorized database updates use the same project/startup pair. The local runner
and deployment documentation use these paths. Startup auto-migration retains
its previous behavior; normal local runner startup keeps it disabled.

Publish Web normally. Both assemblies are normal project references and are
built, listed in deps.json, and copied into publish output in Debug and Release.
Never remove the runtime migrations reference or deploy just the Web DLL.

Ordinary Web C# edits recompile Web, while MSBuild skips unchanged Persistence
and Migrations compilation. Model/migration changes rebuild their dependencies
automatically. Keep `BuildProjectReferences` enabled in the real workflow.
`Rebuild` intentionally recompiles the whole dependency graph and remains slow;
it is not the daily command. A Web-only fresh-compile benchmark may use
`-t:Rebuild -p:BuildProjectReferences=false` **only after** a successful normal
build has refreshed dependencies, with exactly the same flag in the baseline.
This diagnostic does not replace normal validation or hide changed sources.
