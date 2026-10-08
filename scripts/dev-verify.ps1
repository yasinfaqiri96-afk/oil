<#
.SYNOPSIS
One batched development verification; no implicit restore or Graphify.
.EXAMPLE
.\scripts\dev-verify.ps1 -Ui -Paths src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css
.EXAMPLE
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~SalesControllerTests'
.EXAMPLE
.\scripts\dev-verify.ps1 -Full
#>
[CmdletBinding(DefaultParameterSetName = 'Web')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Ui')]
    [switch]$Ui,
    [Parameter(ParameterSetName = 'Web')]
    [switch]$Web,
    [Parameter(Mandatory = $true, ParameterSetName = 'Full')]
    [switch]$Full,
    [Parameter(ParameterSetName = 'Ui')]
    [string[]]$Paths,
    [Parameter(ParameterSetName = 'Ui')]
    [Parameter(ParameterSetName = 'Web')]
    [ValidateNotNullOrEmpty()]
    [string[]]$Filter,
    [switch]$IsolatedCompiler
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webProject = 'src/PTGOilSystem.Web/PTGOilSystem.Web.csproj'
$testProject = 'tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj'
$buildArgs = @('-c', 'Debug', '--no-restore', '-nodeReuse:false')
if ($IsolatedCompiler) {
    $buildArgs += @('-p:UseSharedCompilation=false', '-m:1')
}

function Invoke-VerifyCommand {
    param([string]$Label, [string]$Command, [string[]]$Arguments)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "[$Label] $Command $($Arguments -join ' ')"
    & $Command @Arguments | Out-Host
    $code = $LASTEXITCODE
    $timer.Stop()
    Write-Host ('[{0}] {1:N2}s; exit {2}' -f $Label, $timer.Elapsed.TotalSeconds, $code)
    if ($code -ne 0) {
        throw "$Label failed with exit code $code."
    }
}

$total = [Diagnostics.Stopwatch]::StartNew()
Push-Location $repoRoot
try {
    $needsBuild = $true
    $diffArgs = @('diff', '--check', 'HEAD')
    if ($Ui) {
        if (-not $Paths) {
            $tracked = @(git diff --name-only HEAD)
            if ($LASTEXITCODE -ne 0) { throw 'Unable to read tracked changes.' }
            $untracked = @(git ls-files --others --exclude-standard)
            if ($LASTEXITCODE -ne 0) { throw 'Unable to read untracked changes.' }
            $Paths = @($tracked + $untracked | Sort-Object -Unique)
        }

        $uiExtensions = @('.css', '.js', '.cshtml', '.html', '.md', '.txt', '.svg', '.png', '.jpg', '.jpeg', '.webp', '.gif', '.ico', '.woff', '.woff2', '.ttf')
        foreach ($path in $Paths) {
            $absolutePath = [IO.Path]::GetFullPath((Join-Path $repoRoot $path))
            if (-not $absolutePath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "UI path must be inside this checkout: $path"
            }
            if ([IO.Path]::GetExtension($path) -notin $uiExtensions) {
                throw "UI mode cannot validate $path. Use -Web/-Full, or -Paths for this task's UI files only."
            }
        }
        if ($Paths.Count -eq 0) {
            Write-Host 'No changed UI files to verify.'
            return
        }
        $diffArgs += @('--') + $Paths
        $needsBuild = @($Paths | Where-Object { [IO.Path]::GetExtension($_) -eq '.cshtml' }).Count -gt 0
    }

    Invoke-VerifyCommand -Label 'diff check' -Command 'git' -Arguments $diffArgs

    if ($Ui) {
        foreach ($path in $Paths) {
            if ([IO.Path]::GetExtension($path) -eq '.js' -and (Test-Path -LiteralPath $path)) {
                Invoke-VerifyCommand -Label "JS syntax: $path" -Command 'node' -Arguments @('--check', $path)
            }
        }
        if (-not $needsBuild -and -not $Filter) {
            Write-Host 'UI source checks passed. CSS/visual behavior still needs the relevant browser review; no compilation was needed.'
            return
        }
    }

    if ($Full) {
        Invoke-VerifyCommand -Label 'solution build' -Command 'dotnet' -Arguments (@('build', 'ptg-oil-system.sln') + $buildArgs)
        Invoke-VerifyCommand -Label 'full tests (disposable test databases)' -Command 'dotnet' -Arguments @('test', $testProject, '-c', 'Debug', '--no-build', '--no-restore', '--', 'RunConfiguration.TreatNoTestsAsError=true')
        Invoke-VerifyCommand -Label 'EF pending model check (no apply)' -Command 'dotnet' -Arguments @('ef', 'migrations', 'has-pending-model-changes', '--project', 'src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj', '--startup-project', $webProject, '--configuration', 'Debug', '--no-build')
    }
    elseif ($Filter) {
        # Building the test graph refreshes Web, the importer, and the test DLL in
        # one invocation; do not build Web separately and then rebuild its graph.
        Invoke-VerifyCommand -Label 'Web + test build' -Command 'dotnet' -Arguments (@('build', $testProject) + $buildArgs)
        $testFilter = '(' + ($Filter -join ')|(') + ')'
        Invoke-VerifyCommand -Label 'targeted tests' -Command 'dotnet' -Arguments @('test', $testProject, '-c', 'Debug', '--no-build', '--no-restore', '--filter', $testFilter, '--', 'RunConfiguration.TreatNoTestsAsError=true')
    }
    else {
        Invoke-VerifyCommand -Label 'Web build' -Command 'dotnet' -Arguments (@('build', $webProject) + $buildArgs)
        Write-Host 'Build passed; no test filter supplied, so no tests were run.'
    }
}
finally {
    $total.Stop()
    Write-Host ('[dev verification total] {0:N2}s' -f $total.Elapsed.TotalSeconds)
    Pop-Location
}
