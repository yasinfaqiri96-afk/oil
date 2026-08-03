<#
.SYNOPSIS
Runs the complete test suite for release/deploy verification.

.DESCRIPTION
No category filter is applied. The default reuses an existing Debug test build;
pass -Build after source changes.
#>
[CmdletBinding()]
param([switch]$Build)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\PTGOilSystem.Web.Tests\PTGOilSystem.Web.Tests.csproj'
$assembly = Join-Path $PSScriptRoot '..\tests\PTGOilSystem.Web.Tests\bin\Debug\net8.0\PTGOilSystem.Web.Tests.dll'

if ($Build) {
    dotnet build $project -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $assembly)) {
    throw 'No successful Debug test build exists. Run .\scripts\test-full.ps1 -Build first.'
}

dotnet test $project -c Debug --no-build --no-restore
exit $LASTEXITCODE
