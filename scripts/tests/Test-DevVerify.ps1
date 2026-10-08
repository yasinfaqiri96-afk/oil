# Behavioral checks for command selection and failure propagation. No real
# build, restore, test database, Node process, or Git mutation is performed.
$ErrorActionPreference = 'Stop'
$verifyScript = Join-Path $PSScriptRoot '../dev-verify.ps1'
$recorded = [Collections.Generic.List[string]]::new()
$failBuild = $false
$passed = 0

function git {
    $recorded.Add('git ' + ($args -join ' '))
    $global:LASTEXITCODE = 0
    if (($args -join ' ') -eq 'diff --name-only HEAD') {
        'src/PTGOilSystem.Web/Controllers/SalesController.cs'
    }
}

function dotnet {
    $recorded.Add('dotnet ' + ($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($failBuild -and $args[0] -eq 'build') { $global:LASTEXITCODE = 23 }
}

function node {
    $recorded.Add('node ' + ($args -join ' '))
    $global:LASTEXITCODE = 0
}

function Assert-Commands {
    param([string]$Name, [string[]]$Expected)
    if (($recorded -join "`n") -ne ($Expected -join "`n")) {
        throw "$Name selected unexpected commands: $($recorded -join '; ')"
    }
    $script:passed++
    $recorded.Clear()
}

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "$Name should have failed." }
    $script:passed++
    $recorded.Clear()
}

$web = 'src/PTGOilSystem.Web/PTGOilSystem.Web.csproj'
$tests = 'tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj'
$build = '-c Debug --no-restore -nodeReuse:false'
$css = 'src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css'
$js = 'src/PTGOilSystem.Web/wwwroot/js/core.js'
$razor = 'src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml'

& $verifyScript -Ui -Paths $css
Assert-Commands 'CSS skips compilation' @("git diff --check HEAD -- $css")

& $verifyScript -Ui -Paths $js
Assert-Commands 'JS checks syntax without compilation' @("git diff --check HEAD -- $js", "node --check $js")

& $verifyScript -Ui -Paths $razor
Assert-Commands 'Razor retains compilation' @("git diff --check HEAD -- $razor", "dotnet build $web $build")

& $verifyScript -Web
Assert-Commands 'Web without filter builds only Web' @('git diff --check HEAD', "dotnet build $web $build")

& $verifyScript -Web -Filter 'FullyQualifiedName~First', 'FullyQualifiedName~Second'
Assert-Commands 'Targeted tests build graph once' @(
    'git diff --check HEAD',
    "dotnet build $tests $build",
    "dotnet test $tests -c Debug --no-build --no-restore --filter (FullyQualifiedName~First)|(FullyQualifiedName~Second) -- RunConfiguration.TreatNoTestsAsError=true"
)

& $verifyScript -Ui -Paths $razor -Filter 'FullyQualifiedName~ViewTests'
Assert-Commands 'UI tests do not add a second Web build' @(
    "git diff --check HEAD -- $razor",
    "dotnet build $tests $build",
    "dotnet test $tests -c Debug --no-build --no-restore --filter (FullyQualifiedName~ViewTests) -- RunConfiguration.TreatNoTestsAsError=true"
)

& $verifyScript -Full
Assert-Commands 'Full reuses build and never applies migrations' @(
    'git diff --check HEAD',
    "dotnet build ptg-oil-system.sln $build",
    "dotnet test $tests -c Debug --no-build --no-restore -- RunConfiguration.TreatNoTestsAsError=true",
    "dotnet ef migrations has-pending-model-changes --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project $web --configuration Debug --no-build"
)

& $verifyScript -Web -IsolatedCompiler
Assert-Commands 'Compiler isolation is opt-in' @('git diff --check HEAD', "dotnet build $web $build -p:UseSharedCompilation=false -m:1")

Assert-Rejected 'UI rejects C#' { & $verifyScript -Ui -Paths 'src/PTGOilSystem.Web/Program.cs' } 'UI mode cannot validate'
Assert-Rejected 'UI auto-detection rejects mixed backend changes' { & $verifyScript -Ui } 'UI mode cannot validate'
Assert-Rejected 'UI rejects paths outside checkout' { & $verifyScript -Ui -Paths '../outside.css' } 'inside this checkout'

$failBuild = $true
$rejected = $false
try { & $verifyScript -Web -Filter 'FullyQualifiedName~Any' }
catch {
    if ($_.Exception.Message -notlike '*exit code 23*') { throw }
    $rejected = $true
}
if (-not $rejected -or @($recorded | Where-Object { $_ -like 'dotnet test *' }).Count -ne 0) {
    throw 'A failed build must fail verification and prevent test execution.'
}
$passed++
$global:LASTEXITCODE = 0
Write-Host "PASS: $passed dev-verify behavioral checks."
