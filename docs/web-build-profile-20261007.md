# Web Debug compiler profile — 2026-10-07

## Scope and result

This audit changes no business code, database configuration, migrations, or
runtime behavior. Both experimental compiler GC settings were slower and were
reverted. No compiler, Razor, generator, or C# correctness checks were removed.
No build speed improvement from a retained project change is claimed.

## Measurement

SDK: 8.0.417. Host: 6 physical / 12 logical processors, approximately 14 GiB RAM.
The measured command was:

```powershell
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj -c Debug --no-restore `
  -p:UseSharedCompilation=false -p:ReportAnalyzer=true -nodeReuse:false -m:1 `
  -clp:PerformanceSummary -bl:<audit-directory>/<run>.binlog
```

Project references remained enabled. Only Web ran `Csc`; unchanged Persistence
and Migrations outputs were reused through normal MSBuild dependency checks.
Each fresh run invalidated Web compilation through a project-file edit or a
timestamp-only touch of `Program.cs`; its source content was unchanged. These
diagnostic steps are not recommended as the everyday development workflow.
Stopwatch measured the whole command, including process startup. Compiler
reports came from binary-log replay, not estimates from total build duration.

The initial build was 100.174 seconds with another compiler server active.
After the user stopped concurrent work, the baseline was 94.370 seconds:

| Component | Recorded seconds | Interpretation |
| --- | ---: | --- |
| Web `Csc` | 88.502 | 93.8% of the command's wall time |
| Source generators | 14.432 | Roslyn reported execution time, inside `Csc` |
| Razor source generator | 12.067 | Part of the generator total, inside `Csc` |
| Analyzers | 0.003 | Existing Debug policy; not a current bottleneck |
| ResolveProjectReferences | 1.291 | Aggregate target timing across 3 projects |
| ResolveAssemblyReference | 0.309 | Aggregate task timing across 3 projects |
| GenerateStaticWebAssetsManifest | 0.136 | Target timing |
| GenerateStaticWebAssetsPackFiles | 0.114 | Target timing |
| GenerateStaticWebAssetsDevelopmentManifest | 0.037 | Task timing |
| CopyFilesToOutputDirectory | 0.044 | Aggregate target timing across 3 projects |

Nested/aggregate timings are not independent exclusive costs; do not sum them
or add Razor/generator time to `Csc`. The binlog does not separately time every
C# binding, lowering, emitting, JIT, or GC phase. The additional EventPipe capture
was interrupted during concurrent-process shutdown and is not evidence for a
specific compiler-internal cause.

MSBuild evaluation found 575 Web C# source files, totalling 9,025,292 bytes,
before SDK-generated sources and Razor-generated C#. The build output contains
439 precompiled Razor items. This establishes substantial input size; it does
not prove which individual file or method dominates compilation.

## Rejected experiments

Only the Debug compiler child process received the experimental GC environment;
the application's runtime GC configuration was never changed. The installed
SDK's `Microsoft.CSharp.Core.targets` passes `CscEnvironment` to `Csc` through
`EnvironmentVariables`. Shared compilation was explicitly disabled in all
measurement commands so the compiler child actually received that environment.

| Trial | Whole command | Result |
| --- | ---: | --- |
| Initial baseline | 100.174 s | Build succeeded |
| Server GC limited to 4 heaps | 138.818 s | Slower; reverted |
| Quiet baseline, original settings | 94.370 s | Build succeeded |
| Workstation GC | 122.035 s | 27.666 s / 29.32% slower; reverted |

Both trials produced DLL/PDB files byte-identical to their respective baselines.
All successful fresh builds reported the same 11 warnings and no errors.

The final command with the original settings restored succeeded in **185.462 s**,
with `Csc` at **164.091 s**, generator reporting at **34.033 s**, and Razor
generator reporting at **27.562 s**. This is **not a controlled before/after
comparison**: another Web.Tests build was active and these compile inputs changed
after the quiet baseline, through other concurrent work:

- `Models/Reports/CompanyBalanceReportViewModels.cs`
- `Services/Reporting/CompanyBalanceReportService.cs`
- `Services/Reporting/ProfitAndLossService.ContractEconomics.cs`
- `Services/Reporting/ProfitAndLossService.cs`
- `Views/Reports/CompanyBalance.cshtml`

Those files were not edited or reverted by this audit. Final DLL/PDB hashes differ
from the baseline, confirming that the input/output sets are no longer equivalent.
The raw elapsed difference is +91.092 s (+96.53%), but it cannot be attributed to
a build-setting change or used as an optimization result. **Proven speed saving
from retained build changes: none.** A read-only assembly inspection after the
final successful build again found 439 compiled Razor items, without executing
startup or touching a database.

GC configuration reference:
[Microsoft runtime GC documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector).
Compiler profiling reference:
[Roslyn compiler performance measurement](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Measuring-Compiler-Performance.md).

## Remaining limit

During measurement another `dotnet build` of Web.Tests appeared in the same
checkout, with an active `csc` process using approximately 3.1 GiB RAM. Earlier,
the other compiler server occupied approximately 3.8 GiB. Processes were observed
only; none were killed by this audit. This makes measurements during concurrent
work unsuitable for attributing small time differences to project settings.

Do not run Web and test builds concurrently against this checkout. After a
fresh test-graph build, use targeted tests with `--no-build --no-restore`.
`-IsolatedCompiler` avoids sharing a compiler but does not isolate CPU, RAM, or
the checkout's `bin`/`obj` directories from another build.

The proven dominant step is Web `Csc`, including Razor generation and compilation
of its generated C#. ProjectReference, asset work, copying, and assembly resolution
are too small to explain this latency. Optimizing those steps would not solve it.

All binary logs, text logs, measurements, temporary profiling tools, and baseline
DLL/PDB copies are outside the repository at:
`$env:TEMP/ptg-web-csc-performance-20261007/`. The primary baseline is
`quiet-before.binlog`; the final restored-settings run is `final.binlog`.
