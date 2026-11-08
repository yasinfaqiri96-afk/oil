# راه‌اندازی sender واتساپ: راز مشترک را یک‌بار می‌سازد، وابستگی‌ها را نصب و sender را اجرا می‌کند.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$secret = [Environment]::GetEnvironmentVariable('WA_SENDER_SECRET', 'User')
if ([string]::IsNullOrWhiteSpace($secret)) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $secret = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    [Environment]::SetEnvironmentVariable('WA_SENDER_SECRET', $secret, 'User')
    Write-Host 'WA_SENDER_SECRET ساخته شد. برنامهٔ ASP.NET را یک‌بار ببندید و دوباره اجرا کنید.'
}
$env:WA_SENDER_SECRET = $secret

if (-not (Test-Path node_modules)) {
    npm.cmd install --no-audit --no-fund
}
npm.cmd start
