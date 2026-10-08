# Migration compilation audit — 2026-10-07

## Implemented boundary

277 existing migration/snapshot source files were moved intact to
`src/PTGOilSystem.Migrations/Migrations/`. They represent **143 migration IDs**.
All 277 SHA-256 values match the source manifest captured before the move.
There was no migration regeneration, new migration, SQL execution, database
update, history change, entity definition change, or business-rule change.

The dependency graph is `Web -> Persistence`, `Web -> Migrations -> Persistence`.
The existing DbContext/entity paths and namespaces are retained as linked source
owned by Persistence. The new context partial only supplies the Npgsql migration
assembly default; its original partial declaration and one message helper call
are the only edits inside the original DbContext file. Lock helper types were
extracted from service files without changing their implementations; the existing public period
message method forwards to the extracted implementation. Friend assembly access
preserves internal guard flags without making them public.

See [migration project workflow](migrations-project.md) for EF and deployment
commands. Normal Web builds keep project references enabled: changing a model
or migration still compiles the affected sources. Changing only Web C# does not
compile the million-line migration project again. Both assemblies remain normal
runtime/publish dependencies.

## Actual timings and their limits

Logs, migration source hashes, generated SQL, and MSBuild binary logs were saved
under the local TEMP folder `ptg-migrations-performance-20261007`.
No timing below is a build using stale changed C# sources.

Matched fresh **Web-only** comparison, dependencies already built:

```powershell
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj -c Debug --no-restore -t:Rebuild -p:BuildProjectReferences=false -p:UseSharedCompilation=false -nodeReuse:false -m:1 -clp:PerformanceSummary -bl:<log-path>
```

| Measurement | Before | After |
| --- | ---: | ---: |
| Wall-clock command time | 164.29 s | 148.92 s |
| Web Csc task | 154.45 s | 140.51 s |

Observed reduction: **15.36 seconds / 9.35%**. This single paired measurement
does **not** establish a guaranteed 57-second saving. The first baseline using
the same rebuild settings but without `BuildProjectReferences=false` was
135.65 seconds (Csc 127.77 seconds); it is a distinct command, not the paired
baseline. A graph-wide Rebuild after separation deliberately rebuilds Migrations
too and should not be advertised as the daily speedup. The diagnostic flag above
is never used by the normal verifier or runner.

Timing varied materially with memory pressure and compiler/IDE activity.
Observed free physical memory fell to about 76 MB during compilation, with a
shared VS Code compiler and language server each using roughly 2 GB at one point.
No user process was killed. Do not attribute all differences between runs to the
architecture change.

An actual normal Debug **test-project graph build**, with project references
enabled and fresh Web/test compilation, completed in 138.75 seconds. Its binlog
shows Web Csc **90.14 seconds**, tests Csc **33.26 seconds**, and importer Csc
**4.63 seconds**. **Neither Persistence nor Migrations ran Csc**, and the migration
DLL's timestamp was unchanged. This verifies the daily dependency behavior; the
90.14-second figure is a compiler phase, not a standalone Web command duration.

## Profiling and second bottleneck

The first diagnostic test graph build recorded Web Csc 152.50 s, migration Csc
81.02 s, Persistence Csc 27.68 s, tests Csc 68.49 s, importer Csc 4.48 s.
Across that graph, ResolvePackageAssets was about 0.14 s,
ResolveAssemblyReference 1.43 s and CopyFilesToOutputDirectory 0.16 s. These were
not the dominant costs. Compiler diagnostic reporting identified Web generators
at 18.55 s (Razor source generator 16.15 s); most Web time remained in Csc's
compilation work, including the generated Razor code.

Test analyzers consumed **126.80 seconds of accumulated CPU time**, which is not
126.80 seconds of wall time. Debug test builds now use the same analyzer policy
as Web: `RunAnalyzersDuringBuild=false`; C# compiler diagnostics and generators
remain enabled, and Release still runs all analyzers. The later test analyzer
report was 0.003 s. Test Csc decreased to 33.26 s in the later run; machine load
and cache conditions also changed, so that difference is not a controlled
analyzer-only benchmark.

The importer reference is used by `PartnerSettlementImportTests` and cost about
4.5 s of Csc in the measured graph. It was retained; a separate test architecture
would add complexity for a small measured cost.

A supported separate Razor compilation experiment (`UseRazorSourceGenerator=false`)
was rejected: it took 160.18 s and produced 13 errors because current views rely
on internal Web helpers and Web global usings. The setting was removed; default
Razor compilation and hot reload behavior remain intact. No UI source was edited.
The remaining large bottleneck is Web Csc, with 439 precompiled Razor items and
large application sources, plus machine memory pressure. Further separation
requires its own measured, compatibility-aware design; disabling view checks or
using old DLLs is not an acceptable optimization.

## Compatibility evidence

- `dotnet ef` script generation and pending-model check succeed with the new
  project/startup pair; the full before/after migration SQL has identical SHA-256.
- Read-only local history discovery found the same **143/143 applied migrations**,
  in the same order, with no pending entries. The after connection enforced
  `default_transaction_read_only=on`; startup auto-migration was disabled for EF
  verification. No database update or integration fixture creating a PostgreSQL
  schema was run. Remote production history was not queried or modified.
- 135 focused tests passed with `--no-build --no-restore`, covering discovery and
  snapshot/model compatibility, explicit assembly override, SQLite/InMemory
  provider preservation, lock behavior, model mappings, source-file contracts,
  form concurrency coverage and MVC compiled-view discovery.
- Final normal `dev-verify -Web -IsolatedCompiler` validation succeeded: one
  test graph build (136.68 s), then five compatibility/view tests without rebuild
  (22.20 s), 160.03 s including PowerShell startup. This was a fresh validation
  after the final source formatting, not a warm-cache speed claim.
- Twelve verifier behavior checks and local-runner dependency-input freshness
  checks passed without starting an app or database.
- Release build/publish succeeded with analyzers enabled. Publish contains Web,
  Persistence and Migrations DLLs and their deps.json entries; metadata inspection
  found **143 migration types**, the snapshot, and **439 precompiled Razor items**
  including Layout/Loading/Sales. All three production bundle hashes match their
  generated sources. This is local artifact verification, not a live deployment.

## Changed files (this stage)

- `ptg-oil-system.sln` and `src/PTGOilSystem.Web/PTGOilSystem.Web.csproj`.
- New `src/PTGOilSystem.Persistence/PTGOilSystem.Persistence.csproj` and
  `ApplicationDbContext.Migrations.cs`.
- New `src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj` and the 277
  unchanged migration/snapshot files moved into its `Migrations/` directory.
- Web `Data/ApplicationDbContext.cs`, `Data/ApplicationDbContextFactory.cs`,
  `Program.cs`, `Services/ContractClosure/ContractClosureService.cs`,
  `Services/OperationalPeriod/OperationalPeriodGuard.cs`,
  `Services/OperationalPeriod/OperationalPeriodScope.cs`.
- New Web `Services/ContractClosure/ContractClosureScope.cs` and
  `Services/OperationalPeriod/OperationalPeriodMessage.cs` (extracted helper code).
- `scripts/run-local.ps1`, `scripts/dev-verify.ps1`,
  `scripts/tests/Test-DevVerify.ps1`, new `scripts/tests/Test-LocalRunnerBuildInputs.ps1`.
- Test csproj, `ConcurrencyVersionFormCoverageTests.cs`,
  `ContractBalanceTransfersControllerTests.cs`, `ContractDisplayNameTests.cs`,
  `ShellViewStructureTests.cs`; new `MigrationAssemblyCompatibilityTests.cs` and
  `CompiledViewPackagingTests.cs`.
- `AGENTS.md`, `CLAUDE.md`, `docs/DOMAIN-MODEL.md`, `docs/DEPLOYMENT.md`,
  `docs/development-performance.md`, new `docs/migrations-project.md` and this report.

Entity definitions and the other linked helper sources have no content edits.
Unrelated pre-existing business/UI changes were preserved.
