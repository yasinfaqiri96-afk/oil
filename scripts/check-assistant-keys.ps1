# ============================================================
#  PTG Oil System - assistant API key check
#
#  Reports which assistant provider keys are visible, without
#  printing any key value. Read-only: changes nothing.
# ============================================================

$ErrorActionPreference = "Stop"

function Show-Variable {
    param([string]$Name)

    Write-Host ""
    Write-Host $Name -ForegroundColor Cyan

    foreach ($scope in @("Process", "User", "Machine")) {
        $value = if ($scope -eq "Process") {
            [Environment]::GetEnvironmentVariable($Name)
        }
        else {
            [Environment]::GetEnvironmentVariable($Name, $scope)
        }

        if ([string]::IsNullOrWhiteSpace($value)) {
            Write-Host ("  {0,-9} : not set" -f $scope) -ForegroundColor DarkGray
        }
        else {
            $masked = $value.Substring(0, [Math]::Min(4, $value.Length)) + "..." + `
                      $value.Substring([Math]::Max(0, $value.Length - 4))
            Write-Host ("  {0,-9} : set (length {1}, {2})" -f $scope, $value.Length, $masked) -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "PTG assistant provider keys" -ForegroundColor Cyan

Show-Variable "GEMINI_API_KEY"
Show-Variable "GROQ_API_KEY"
Show-Variable "ANTHROPIC_API_KEY"

Write-Host ""
Write-Host "The app only sees the 'Process' value. If a User-scope key is set but the"
Write-Host "process value is empty, the terminal or app was started before the key"
Write-Host "existed - restart it."
Write-Host ""
Write-Host "Which key is required depends on appsettings.json:"
Write-Host "  Assistant:Provider          -> primary provider"
Write-Host "  Assistant:FallbackProvider  -> used only on timeout / 429 / 5xx"
Write-Host ""
