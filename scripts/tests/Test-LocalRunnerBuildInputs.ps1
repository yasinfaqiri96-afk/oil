$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot '../run-local.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$null, [ref]$null)
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-LocalBuildRequired'
}, $false)
# Load only the pure freshness check. Never execute runner startup/EF/database code.
Invoke-Expression $function.Extent.Text
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ptg-runner-inputs-' + [Guid]::NewGuid().ToString('N'))
$web = Join-Path $fixture 'src/PTGOilSystem.Web'
$output = Join-Path $web 'bin/Debug/net8.0'
$dll = Join-Path $output 'PTGOilSystem.Web.dll'
try {
    foreach ($dir in @($output, (Join-Path $fixture 'src/PTGOilSystem.Persistence'), (Join-Path $fixture 'src/PTGOilSystem.Migrations/Migrations'))) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if (-not (Test-LocalBuildRequired $web $dll)) { throw 'Missing Web output was accepted.' }
    Set-Content -LiteralPath $dll -Value 'fixture'
    if (-not (Test-LocalBuildRequired $web $dll)) { throw 'Missing dependency output was accepted.' }
    foreach ($name in @('PTGOilSystem.Persistence.dll', 'PTGOilSystem.Migrations.dll')) {
        Set-Content -LiteralPath (Join-Path $output $name) -Value 'fixture'
    }
    if (Test-LocalBuildRequired $web $dll) { throw 'Current empty fixture unexpectedly requires build.' }
    foreach ($relative in @('src/PTGOilSystem.Persistence/Configuration.cs', 'src/PTGOilSystem.Migrations/Migrations/Existing.cs')) {
        $source = Join-Path $fixture $relative
        Set-Content -LiteralPath $source -Value 'fixture'
        (Get-Item -LiteralPath $source).LastWriteTimeUtc = (Get-Item -LiteralPath $dll).LastWriteTimeUtc.AddSeconds(1)
        if (-not (Test-LocalBuildRequired $web $dll)) { throw "Changed dependency was missed: $relative" }
        (Get-Item -LiteralPath $source).LastWriteTimeUtc = (Get-Item -LiteralPath $dll).LastWriteTimeUtc.AddSeconds(-1)
    }
    if (Test-LocalBuildRequired $web $dll) { throw 'Older dependency unexpectedly requires build.' }
    Write-Host 'PASS: local runner detects missing outputs and changed dependency sources without starting an app/database.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'ptg-runner-inputs-*') {
        throw 'Unsafe fixture cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
