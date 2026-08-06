# ============================================================
#  PTG Oil System - GROQ_API_KEY setup (one time)
#
#  Sets GROQ_API_KEY permanently for the current Windows user.
#  The key is read from the console and is never written to a
#  file, never printed, and never committed to git.
#
#  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-groq-key.ps1
# ============================================================

[CmdletBinding()]
param(
    [ValidateSet("User", "Machine")]
    [string]$Scope = "User"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "PTG Oil System - GROQ_API_KEY setup" -ForegroundColor Cyan
Write-Host "Scope: $Scope"
Write-Host ""

$existing = [Environment]::GetEnvironmentVariable("GROQ_API_KEY", $Scope)
if ($existing) {
    Write-Host "A key is already set for this scope (length $($existing.Length))." -ForegroundColor Yellow
    $overwrite = Read-Host "Overwrite it? (y/N)"
    if ($overwrite -ne "y" -and $overwrite -ne "Y") {
        Write-Host "Cancelled. Nothing was changed." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Paste the Groq API key (input is hidden), then press Enter:"
$secure = Read-Host -AsSecureString

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$plain = $plain.Trim()

if ([string]::IsNullOrWhiteSpace($plain)) {
    Write-Host "No key entered. Nothing was changed." -ForegroundColor Red
    exit 1
}

if (-not $plain.StartsWith("gsk_")) {
    Write-Host "Warning: Groq keys normally start with 'gsk_'. Continuing anyway." -ForegroundColor Yellow
}

[Environment]::SetEnvironmentVariable("GROQ_API_KEY", $plain, $Scope)

# Also set it for this session so an immediate test run works.
$env:GROQ_API_KEY = $plain
$plain = $null

Write-Host ""
Write-Host "Done. GROQ_API_KEY is now stored for the $Scope scope." -ForegroundColor Green
Write-Host ""
Write-Host "IMPORTANT: close every open terminal, editor and running PTG window," -ForegroundColor Yellow
Write-Host "then start the app again. Already-running processes keep the old,"     -ForegroundColor Yellow
Write-Host "empty environment and will still show the 'key not set' message."      -ForegroundColor Yellow
Write-Host ""
Write-Host "Verify later with:  scripts\check-groq-key.ps1"
Write-Host ""
