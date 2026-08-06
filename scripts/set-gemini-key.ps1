# ============================================================
#  PTG Oil System - GEMINI_API_KEY setup (one time)
#
#  Sets GEMINI_API_KEY permanently for the current Windows user.
#  The key is read from the console and is never written to a
#  file, never printed, and never committed to git.
#
#  Get a key at: https://aistudio.google.com/apikey
#
#  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-gemini-key.ps1
# ============================================================

[CmdletBinding()]
param(
    [ValidateSet("User", "Machine")]
    [string]$Scope = "User"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "PTG Oil System - GEMINI_API_KEY setup" -ForegroundColor Cyan
Write-Host "Scope: $Scope"
Write-Host ""

$existing = [Environment]::GetEnvironmentVariable("GEMINI_API_KEY", $Scope)
if ($existing) {
    Write-Host "A key is already set for this scope (length $($existing.Length))." -ForegroundColor Yellow
    $overwrite = Read-Host "Overwrite it? (y/N)"
    if ($overwrite -ne "y" -and $overwrite -ne "Y") {
        Write-Host "Cancelled. Nothing was changed." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Paste the Gemini API key (input is hidden), then press Enter:"
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

# Google AI Studio issues more than one key format: the long-standing 'AIza...'
# keys and the newer 'AQ.' keys. Both are valid, so only an unrecognised prefix
# is worth mentioning.
if (-not ($plain.StartsWith("AIza") -or $plain.StartsWith("AQ."))) {
    Write-Host "Warning: this does not look like a Google AI Studio key (expected 'AIza...' or 'AQ....'). Continuing anyway." -ForegroundColor Yellow
}

[Environment]::SetEnvironmentVariable("GEMINI_API_KEY", $plain, $Scope)

# Also set it for this session so an immediate test run works.
$env:GEMINI_API_KEY = $plain
$plain = $null

Write-Host ""
Write-Host "Done. GEMINI_API_KEY is now stored for the $Scope scope." -ForegroundColor Green
Write-Host ""
Write-Host "IMPORTANT: close every open terminal, editor and running PTG window," -ForegroundColor Yellow
Write-Host "then start the app again. Already-running processes keep the old,"     -ForegroundColor Yellow
Write-Host "empty environment and will still show the 'key not set' message."      -ForegroundColor Yellow
Write-Host ""
Write-Host "Verify later with:  scripts\check-assistant-keys.ps1"
Write-Host ""
