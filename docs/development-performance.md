# Development verification

Use `inspect once → batch edits → validate once`. Locate the relevant method
with `rg` and read its surrounding lines. Stop exploration when the root cause
is known. Keep unrelated working-tree changes intact. Graphify is optional for
explicit requests and substantial architecture/dependency work; it is never a
delivery gate for an ordinary fix.

## Commands

Run from PowerShell; use `powershell -NoProfile -ExecutionPolicy Bypass -File`
before a script path if the local execution policy requires it.

```powershell
# CSS/JS/text: scoped diff check; JavaScript also gets node --check.
.\scripts\dev-verify.ps1 -Ui -Paths src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css

# Razor: same UI mode, with one Web build to preserve compile validation.
.\scripts\dev-verify.ps1 -Ui -Paths src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml

# C# or Razor with relevant tests: one build of Web + tests, then no rebuild.
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~RelatedClass'

# No tests requested: Web only.
.\scripts\dev-verify.ps1 -Web

# After that fresh test build, rerun any related tests without rebuilding.
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~RelatedClass.Method'

# Only shared/model/migration/cross-cutting or release verification:
.\scripts\dev-verify.ps1 -Full

# Compiler contention fallback for this invocation; do not kill other processes.
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~RelatedClass' -IsolatedCompiler
```

`-Filter` accepts multiple filters and combines them in one test run. With a
filter, dev-verify builds the test project and its references once, rather than
building Web and then implicitly building it again with `dotnet test`. Without
a filter, it builds only Web. Failed checks stop the workflow before later steps;
a filter matching no tests also fails instead of reporting a false success.

`-Ui` without `-Paths` inspects tracked and untracked changes. It refuses mixed
backend/project changes; use explicit task paths in a dirty checkout. CSS and
text checks are lightweight source checks, not a browser/visual test. JavaScript
requires Node. Razor compilation remains enabled. Use relevant structural tests
when the changed markup requires them. Full verification runs PostgreSQL-backed
tests that create/delete disposable test databases; configure the test fixture's
test connection, never a production database. The EF check does not apply migrations.

The existing `test-fast.ps1`, `test-full.ps1`, and `test-accounting.ps1` already
use `--no-build --no-restore` by default and remain unchanged. Their `-Build`
option is needed after relevant source changes. A standalone Web build does not
refresh the test assembly. Do not treat a stale test DLL as verification of edits.

No routine command restores packages automatically. Restore the relevant project
once after dependency/build-import changes or missing/stale NuGet assets, then
resume commands with `--no-restore`. Do not use a full solution restore/build
for an ordinary Web-only task.

## Build and production boundary

`_Layout.cshtml` loads individual CSS/JS files in Development and the three
generated bundles in other environments. Debug builds skip `BuildCssBundles`;
Release builds and Publish generate the bundles. Publish explicitly includes
the three generated files, including a Debug publish reusing a prior build.
When running Debug outside Development, opt in explicitly:

```powershell
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-restore -p:PtgBuildBundles=true
```

Debug analyzers were already disabled. Razor compilation and source generators,
portable debugging symbols, and shared compilation remain enabled. There is no
evidence that disabling them would preserve reliable validation and debugging.
The RuntimeCompilation package is retained; Program.cs does not explicitly call
`AddRazorRuntimeCompilation`. Its imported targets preserve compilation references
and metadata. No runtime or hosting configuration was changed.

Do not run two writing agents on the same checkout. Separate Git worktrees or
checkouts isolate source and build outputs; changing branches in the same folder
does not. VS Code owns its project-system/MSBuild processes. Shared compiler
servers can intentionally persist for reuse. Dev-verify disables MSBuild node
reuse for its builds; `-IsolatedCompiler` additionally disables compiler sharing
and uses one build worker. No process cleanup is performed by the script.

## Migration boundary

The Web project's migration sources are retained unchanged. EF Core supports a
[separate migrations project](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects),
but this application owns its DbContext in Web and loads migrations at runtime.
Separating them requires dependency/design-time/provider/deployment changes and
avoiding a circular reference. That architecture change is deferred; it is not a
safe toggle for ordinary development builds. Never exclude migrations from a
real Web build or deployment artifact.

## Measurement

The audit uses .NET SDK 8.0.417 on this Windows checkout. Real command wall time
is recorded with Stopwatch, including process startup. A controlled Web Debug
rebuild uses the same options before/after:

```powershell
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj -c Debug --no-restore -t:Rebuild -p:UseSharedCompilation=false -nodeReuse:false -m:1 -clp:PerformanceSummary
```

The targeted test is
`ShellViewStructureTests.Shared_PageFrame_Owns_Workspace_Width_Gutters_And_Form_Columns`.
Its with-build command uses `--no-restore` and the same isolated compiler options;
the repeat uses `--no-build --no-restore`. Baseline Web rebuild: 153.45 seconds,
including 144.56 seconds in Csc and 2.55 seconds in BuildCssBundles. Baseline
targeted command with build: 58.41 seconds; without build: 4.76 seconds.

The new CSS-only verifier takes 1.04 seconds including launching PowerShell
(0.35 seconds inside the script). No old dev-verify script existed; a sum of old
build/test commands is an equivalent workflow measurement, not a benchmark of
an old script. Incremental/test timings depend on fresh outputs and machine load;
do not attribute cache warm-up to the optimization.

| Operation | Before (seconds) | After (seconds) |
| --- | ---: | ---: |
| Controlled Web Debug rebuild | 153.45 | 143.43 |
| Targeted test command with build | 58.41 | 53.71 |
| Same test without build/restore | 4.76 | 4.64 |
| CSS-only dev-verify, including PowerShell startup | No prior script | 1.04 |
| Web + targeted dev-verify, compilation required | No prior script | 152.83 |
| Web + targeted dev-verify, outputs current | No prior script | 10.90 |

The old two-command workflow's measured build/test phases sum to 211.86 seconds
(153.45 + 58.41). This is an equivalent workflow baseline, not a direct comparison
to the warm verifier: initial test-output state and cache freshness differ. The
observed 10.02-second rebuild reduction is not entirely attributable to the
2.55-second bundling removal; Csc also varied from 144.56 to 136.09 seconds. The
guaranteed change is removal of the unnecessary phase, not a fixed percentage
speedup for every machine. New C# inputs still require real compilation.

A compile-only diagnostic in a separate TEMP intermediate/output directory
removed migration files from that experiment's Compile items only. Csc was
87.27 seconds versus 144.56 seconds with migrations in the baseline. This
single-run comparison suggests a material migration cost (roughly 57 seconds),
but also substantial remaining Razor/application compilation. No repository
migration file, real build setting, schema, history, or database was changed.
The diagnostic assembly was never run, tested, published, or copied into Web's
output. This was evidence for the subsequent migration assembly separation, described
in `docs/migrations-project.md`; the diagnostic itself was never deployed.

Validation includes twelve script command-selection/failure-propagation checks,
the actual targeted test, and an intentional nonmatching filter confirming
`TreatNoTestsAsError=true` returns failure. No full suite, database migration,
production deployment, or Graphify benchmark is required for these changes.
Local publish output checks cover both Release and Debug with `--no-build` and
compare all three bundle files with their generated source hashes. These checks
verify artifact compatibility; they do not claim a live production/runtime test.
Release publish completed successfully in 161.60 seconds; Debug publish without
building completed in 6.60 seconds. Both contain the Web assembly and all three
nonempty bundles with matching SHA-256 hashes. The final scoped diff/format check
passed for all thirteen task files. Existing business/UI edits were preserved.
