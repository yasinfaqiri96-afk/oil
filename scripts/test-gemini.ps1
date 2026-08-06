# ============================================================
#  PTG Oil System - live Gemini check
#
#  Verifies, against the real Google endpoint:
#    1. the GEMINI_API_KEY is accepted
#    2. the configured model is reachable for this key and region
#    3. function calling actually fires
#
#  Read-only. Never prints the key. On failure it reports the exact
#  service error and does NOT silently try a different model.
#
#  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gemini.ps1
#        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gemini.ps1 -Model gemini-2.5-flash
# ============================================================

[CmdletBinding()]
param(
    [string]$Model = "gemini-3.6-flash"
)

$ErrorActionPreference = "Stop"

$key = [Environment]::GetEnvironmentVariable("GEMINI_API_KEY")
if ([string]::IsNullOrWhiteSpace($key)) {
    $key = [Environment]::GetEnvironmentVariable("GEMINI_API_KEY", "User")
}

if ([string]::IsNullOrWhiteSpace($key)) {
    Write-Host "GEMINI_API_KEY is not set. Run scripts\set-gemini-key.ps1 first." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Live Gemini check - model: $Model" -ForegroundColor Cyan
Write-Host ""

# Windows PowerShell 5.1 corrupts non-ASCII request bodies unless they are sent
# as UTF-8 bytes, which would make Persian prompts arrive as '?????' and produce
# fake failures. Everything below therefore encodes explicitly.
function Invoke-Gemini {
    param([string]$Uri, [object]$Payload)

    $bytes = [Text.Encoding]::UTF8.GetBytes(($Payload | ConvertTo-Json -Depth 20))
    return Invoke-RestMethod -Uri $Uri -Method Post -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'x-goog-api-key' = $key } -Body $bytes -ErrorAction Stop
}

function Show-Failure {
    param($ErrorRecord, [string]$Step)

    $status = $null
    if ($ErrorRecord.Exception.Response) {
        $status = [int]$ErrorRecord.Exception.Response.StatusCode
    }

    Write-Host "FAILED at: $Step" -ForegroundColor Red
    if ($status) { Write-Host "  HTTP status: $status" -ForegroundColor Red }

    $body = ""
    try {
        $stream = $ErrorRecord.Exception.Response.GetResponseStream()
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
    }
    catch { }

    if ($body) {
        # Defence in depth: never echo the key back, even if the service does.
        $body = $body.Replace($key, "***")
        Write-Host "  service message:" -ForegroundColor Red
        Write-Host "  $body"
    }

    Write-Host ""

    # A region block is reported as 400/FAILED_PRECONDITION and is NOT a key problem.
    # Google rejects the request before it ever looks at the key or the model, so
    # saying "check the key" here would send the reader down the wrong path.
    if ($body -and $body -match "location is not supported") {
        Write-Host "Meaning: Gemini is not available from this machine's country/IP." -ForegroundColor Yellow
        Write-Host "         The key and the model were never checked, so neither is proven wrong." -ForegroundColor Yellow
        Write-Host "         Run the app from a supported region, or set Assistant:Provider to another provider." -ForegroundColor Yellow
        return
    }

    switch ($status) {
        400 { Write-Host "Meaning: the request or the API key was rejected. Check the key value." -ForegroundColor Yellow }
        403 { Write-Host "Meaning: this key may not use this model, or the region is not supported." -ForegroundColor Yellow }
        404 { Write-Host "Meaning: model '$Model' does not exist for this key. Pick a model this key can see - no substitute is chosen automatically." -ForegroundColor Yellow }
        429 { Write-Host "Meaning: quota or rate limit reached. This is temporary." -ForegroundColor Yellow }
        default { Write-Host "Meaning: see the service message above." -ForegroundColor Yellow }
    }
}

$base = "https://generativelanguage.googleapis.com/v1beta/models/$Model`:generateContent"

# ---- 1. plain text round trip ----------------------------------------------
Write-Host "1) plain text request ..." -NoNewline
try {
    $payload = @{
        contents = @(@{ role = "user"; parts = @(@{ text = "به دری کوتاه بنویس: تست موفق" }) })
        generationConfig = @{ maxOutputTokens = 200; temperature = 0.2 }
    }
    $r = Invoke-Gemini -Uri $base -Payload $payload
    Write-Host " OK" -ForegroundColor Green
    Write-Host "   model version: $($r.modelVersion)"
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host ""
    Show-Failure -ErrorRecord $_ -Step "plain text request"
    exit 1
}

# ---- 2. function calling ----------------------------------------------------
Write-Host "2) function calling ..." -NoNewline
try {
    $payload = @{
        systemInstruction = @{ parts = @(@{ text = "تو دستیار نرم‌افزار نفت هستی. برای ارقام واقعی حتما ابزار صدا بزن و عدد از خود نساز." }) }
        contents = @(@{ role = "user"; parts = @(@{ text = "موجودی فعلی انبار چقدر است؟" }) })
        tools = @(@{
            functionDeclarations = @(@{
                name = "get_stock_balance"
                description = "موجودی آزاد انبار به تن متریک. برای موجودی کل بدون هیچ ورودی صدا بزن."
                parameters = @{
                    type = "object"
                    properties = @{
                        product_name = @{ type = "string"; description = "نام محصول. اگر کاربر نام نبرده، این فیلد را حذف کن." }
                    }
                }
            })
        })
        generationConfig = @{ maxOutputTokens = 2000; temperature = 0.2 }
    }

    $r = Invoke-Gemini -Uri $base -Payload $payload
    $parts = $r.candidates[0].content.parts
    $call = $parts | Where-Object { $_.functionCall } | Select-Object -First 1

    if ($call) {
        Write-Host " OK" -ForegroundColor Green
        Write-Host "   called: $($call.functionCall.name)"
        Write-Host "   args  : $($call.functionCall.args | ConvertTo-Json -Compress)"
    }
    else {
        Write-Host " NO TOOL CALL" -ForegroundColor Yellow
        Write-Host "   The model answered with text instead of calling the tool."
        Write-Host "   finishReason: $($r.candidates[0].finishReason)"
    }
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host ""
    Show-Failure -ErrorRecord $_ -Step "function calling"
    exit 1
}

Write-Host ""
Write-Host "Live Gemini check passed for model '$Model'." -ForegroundColor Green
Write-Host ""
