# ============================================================
#  PTG Oil System - GROQ_API_KEY check
#
#  Reports whether the key is visible, without printing it.
#  Read-only: changes nothing.
# ============================================================

$ErrorActionPreference = "Stop"

function Show-Scope {
    param([string]$Label, [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        Write-Host ("{0,-22} : not set" -f $Label) -ForegroundColor Red
    }
    else {
        $masked = $Value.Substring(0, [Math]::Min(4, $Value.Length)) + "..." + `
                  $Value.Substring([Math]::Max(0, $Value.Length - 4))
        Write-Host ("{0,-22} : set (length {1}, {2})" -f $Label, $Value.Length, $masked) -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "GROQ_API_KEY visibility" -ForegroundColor Cyan
Write-Host ""

Show-Scope "Current process"  $env:GROQ_API_KEY
Show-Scope "User scope"       ([Environment]::GetEnvironmentVariable("GROQ_API_KEY", "User"))
Show-Scope "Machine scope"    ([Environment]::GetEnvironmentVariable("GROQ_API_KEY", "Machine"))

Write-Host ""
Write-Host "The app only sees the 'Current process' value. If User scope is set"
Write-Host "but the process value is empty, the terminal or app was started before"
Write-Host "the key existed - restart it."
Write-Host ""
