# PTG WhatsApp sender با Baileys

این sender غیررسمی است و فقط باید با یک شمارهٔ جداگانهٔ واتساپ استفاده شود. Chrome، Puppeteer و WhatsApp Web لازم نیست؛ فقط Node.js و یک‌بار اسکن QR لازم است. احتمال محدود یا مسدودشدن شماره وجود دارد.

## اجرا

```powershell
cd integrations/whatsapp-unofficial
npm.cmd install --no-audit --no-fund
$env:WA_SENDER_SECRET = "یک-راز-طولانی-محلی"
$env:WA_MIN_INTERVAL_MS = "60000"
$env:WA_MAX_QUEUE = "100"
npm.cmd start
```

QR را با شمارهٔ جداگانه اسکن کنید. نشست در `.baileys_auth` ذخیره می‌شود و در `.gitignore` قرار دارد؛ آن را به کسی ندهید.

پس از پیام `Baileys WhatsApp sender ready`، پروژهٔ ASP.NET را در PowerShell جدا با همان `WA_SENDER_SECRET` اجرا کنید. endpointهای `/health` و `/send-receipt` روی localhost هستند و کد C# پس از Commit موفق پرداخت یا فروش، رسید را وارد صف می‌کند.
