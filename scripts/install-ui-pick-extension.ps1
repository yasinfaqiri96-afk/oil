<#
.SYNOPSIS
    Installs the local PTG UI Pick Bridge extension into VS Code.

.DESCRIPTION
    Copies tools/vscode-ptg-ui-pick into the user's VS Code extensions folder as
    an unpacked extension. No packaging tool and no extra npm dependency needed.
    Re-run it after changing the extension, then reload the VS Code window.

.PARAMETER Uninstall
    Removes the installed copy instead of installing it.
#>
param(
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot "tools\vscode-ptg-ui-pick"

if (-not (Test-Path (Join-Path $source "package.json"))) {
    throw "Extension source not found at $source"
}

$manifest = Get-Content (Join-Path $source "package.json") -Raw | ConvertFrom-Json
$folderName = "$($manifest.publisher).$($manifest.name)-$($manifest.version)"
$target = Join-Path $env:USERPROFILE ".vscode\extensions\$folderName"

if ($Uninstall) {
    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
        Write-Host "Removed $target"
    }
    else {
        Write-Host "Nothing to remove at $target"
    }
    Write-Host "Reload the VS Code window to finish."
    return
}

# Drop older versions of the same extension so VS Code does not load two copies.
$extensionsDir = Join-Path $env:USERPROFILE ".vscode\extensions"
Get-ChildItem $extensionsDir -Directory -Filter "$($manifest.publisher).$($manifest.name)-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $folderName } |
    ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        Write-Host "Removed old version: $($_.Name)"
    }

if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $source "*") $target -Recurse -Force

Write-Host "Installed PTG UI Pick Bridge -> $target"
Write-Host ""
Write-Host "Next: reload the VS Code window (Ctrl+Shift+P -> 'Developer: Reload Window')."
