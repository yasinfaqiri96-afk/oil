# UI Pick — Browser → Claude Code workflow

هدف: یک عنصر را در مرورگر انتخاب کنی، Claude Code همان عنصر را در کد پیدا کند،
تحلیل کند و فقط همان قسمت را اصلاح کند.

```
VS Code  →  run app (dev watch)  →  Browser  →  Alt+Shift+P + click
        →  .ptg-ui-pick/last-pick.json  →  /ui-pick در Claude Code
        →  پیدا کردن View/Partial/CSS/JS  →  ویرایش  →  build/test  →  Refresh در مرورگر
```

## اجزا

| جزء | فایل | نقش |
|---|---|---|
| Picker overlay | `src/PTGOilSystem.Web/wwwroot/js/dev/ptg-ui-pick.js` | انتخاب عنصر در مرورگر و جمع‌آوری اطلاعات |
| Pick server | `tools/ui-pick/server.mjs` | دریافت انتخاب و نوشتن آن در `.ptg-ui-pick/` |
| Partial markers | `src/PTGOilSystem.Web/TagHelpers/UiPickPartialMarkerTagHelper.cs` | مشخص کردن مرز هر Partial با HTML comment |
| View marker | `Views/Shared/_Layout.cshtml` → `<body data-ptg-view=…>` | مسیر View اصلی صفحه |
| VS Code bridge | `tools/vscode-ptg-ui-pick/` | تحویل خودکار Pick به پنل Claude Code |
| Slash command | `.claude/commands/ui-pick.md` | دستور `/ui-pick` در Claude Code |
| Browser MCP | `.mcp.json` → `playwright` | باز کردن صفحه، Screenshot و بررسی نتیجه توسط Claude |

همهٔ بخش‌های سمت سرور فقط وقتی فعال‌اند که:
`ASPNETCORE_ENVIRONMENT=Development` **و** `PTG_UI_PICK=1`.
در Production هیچ‌کدام اجرا نمی‌شوند و هیچ خروجی اضافه‌ای تولید نمی‌کنند.

## نصب یک‌بارهٔ VS Code Bridge

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-ui-pick-extension.ps1
```

یا task **PTG: Install UI Pick Bridge (VS Code extension)**. بعد از نصب:
`Ctrl+Shift+P` → `Developer: Reload Window`.
حذف: همان اسکریپت با `-Uninstall`.

### چه چیزی از Claude Code Extension واقعاً موجود است

بررسی روی `anthropic.claude-code@2.1.233` (`package.json` + `extension.js` + `webview/index.js`):

| قابلیت | وضعیت | Command رسمی |
|---|---|---|
| Focus کردن input پنل Claude | ✅ | `claude-vscode.focus` |
| باز کردن Tab جدید Claude با Prompt از پیش نوشته‌شده | ✅ | `claude-vscode.editor.open(sessionId, prompt, viewColumn)` |
| باز کردن پنل/سایدبار/ترمینال | ✅ | `claude-vscode.sidebar.open` / `editor.open` / `terminal.open` |
| نوشتن متن دلخواه در **session باز فعلی** | ❌ | ندارد |
| ارسال خودکار پیام (Send) | ❌ | ندارد |
| Extension API عمومی (`exports`) | ❌ | `module.exports = { activate, deactivate }` |

خودِ Extension هم این محدودیت را صریح می‌گوید؛ اگر session از قبل باز باشد پیام
می‌دهد: *"Session is already open. Your prompt was not applied — enter it manually."*
`initialPrompt` هم در webview با `setInputText` فقط **تایپ** می‌شود، نه ارسال.

بنابراین Bridge از همان دو Command رسمی + Clipboard استفاده می‌کند. هیچ
UI automation، شبیه‌سازی کیبورد/ماوس یا دستکاری داخلی Extension انجام نمی‌شود.

## حالت‌های تحویل

`ptgUiPick.deliveryMode` در Settings:

| مقدار | رفتار | کار دستی تو |
|---|---|---|
| `focusAndClipboard` (پیش‌فرض) | Prompt در Clipboard + Focus روی input همین گفتگو | `Ctrl+V` سپس `Enter` |
| `newClaudeTab` | Tab جدید Claude با Prompt از پیش نوشته‌شده | فقط `Enter` |
| `clipboardOnly` | فقط Clipboard | `Ctrl+V` سپس `Enter` |

سایر تنظیمات: `ptgUiPick.autoDeliver` (پیش‌فرض `true`)،
`ptgUiPick.promptStyle` = `summary` (پیش‌فرض) یا `full`.

Commandها: `PTG: Send Selected UI Element to Claude Code` (**`Alt+Shift+C`**)،
`PTG: Copy Last UI Pick to Clipboard`، `PTG: Open Last UI Pick File`،
`PTG: Toggle Automatic UI Pick Delivery`.

## راه‌اندازی روزانه

1. در VS Code: `Ctrl+Shift+B` (یا `Terminal → Run Task…` → **PTG: UI Dev (app + pick server)**).
   این task هم‌زمان اجرا می‌کند:
   - `PTG: UI Pick Server` → `node tools/ui-pick/server.mjs` روی `127.0.0.1:5199`
   - `PTG: Web (dev watch + UI Pick)` → `run-dev.bat` با `PTG_UI_PICK=1` روی `http://localhost:5000`
2. مرورگر: `F5` → **PTG: Open app in Chrome (UI inspect)** یا مستقیم `http://localhost:5000`.
3. Login و رفتن به صفحهٔ موردنظر.

## انتخاب یک عنصر

| کلید | کار |
|---|---|
| `Alt+Shift+P` | روشن/خاموش کردن حالت انتخاب (یا کلیک روی نشان «UI Pick» پایین-چپ) |
| کلیک | ثبت عنصر زیر نشانگر |
| `Alt+Shift+L` | کپی دوبارهٔ آخرین انتخاب در Clipboard |
| `Esc` | خروج از حالت انتخاب |

پس از کلیک، فایل‌های زیر نوشته می‌شوند:

- `.ptg-ui-pick/last-pick.md` — خلاصهٔ کوتاه (Claude اول این را می‌خواند)
- `.ptg-ui-pick/last-pick.json` — payload کامل
- `.ptg-ui-pick/history/pick-<timestamp>.json` — ۵۰ انتخاب آخر

Bridge بلافاصله فایل را می‌بیند، Prompt را در Clipboard می‌گذارد و input پنل
Claude Code را Focus می‌کند. در پنل فقط:

```text
Ctrl+V  →  ادامهٔ جمله را تایپ کن  →  Enter
```

مثلاً بعد از Paste چنین چیزی در input است:

```text
/ui-pick nav.ak-detail-actionbar — view: ~/Views/Shared/Partials/_DetailActionBar.cshtml — http://localhost:5000/LoadingReceipts/Details/2 —
```

و تو ادامه می‌دهی: «این قسمت را مدرن‌تر کن».

اگر Focus خودکار را نمی‌خواهی: `Alt+Shift+C` هر وقت خواستی آخرین Pick را تحویل می‌دهد.

## اطلاعاتی که ثبت می‌شود

`page`: url، path، title، controller/action، viewport، scroll، dir، lang
`element`: tag، id، classes، همهٔ `data-*`، سایر attributeها، متن، CSS selector، مختصات و ابعاد، `outerHTML` (تا ۴۰۰۰ کاراکتر)
`source`: مسیر View، زنجیرهٔ Partialها، classهای غیرعمومی برای grep، فایل‌های JS بارگذاری‌شدهٔ صفحه، stylesheetها
`ancestors` / `children`: تا ۸ والد و ۱۲ فرزند
`computedStyles`: ۳۰ خاصیت کلیدی (layout، typography، رنگ، border، shadow)

## محدودیت‌های شناخته‌شده

| مورد | وضعیت | جایگزین |
|---|---|---|
| `<partial name="…" />` (۸۸۴ مورد) | ✅ مرزگذاری می‌شود | — |
| `@await Html.PartialAsync(…)` (۱۸۳ مورد) | ❌ مرزگذاری نمی‌شود | `source.view` صفحه + `classHints` برای grep |
| `<vc:… />` ViewComponent (۳۰۷ مورد) | ❌ مرزگذاری نمی‌شود | `classHints` + `ViewComponents/` |
| شمارهٔ خط دقیق در فایل | ❌ Razor آن را به DOM نمی‌دهد | grep روی `id` / class / متن عنصر |
| Screenshot از داخل picker | ❌ محدودیت مرورگر | Playwright MCP |
| ارسال خودکار پیام به session باز Claude | ❌ API رسمی ندارد | `Ctrl+V`+`Enter` یا `newClaudeTab` |

دلیل نبودن مرزگذاری روی همهٔ elementها: افزودن TagHelper عمومی روی
`div/input/button/…` در این پروژه باعث خطای Razor `RZ1031` می‌شود، چون در بسیاری
از Viewها داخل ناحیهٔ attribute کدِ C# (`@if`, `@(...)`) وجود دارد. به همین دلیل
فقط `<partial>` علامت‌گذاری می‌شود که ریسک صفر دارد.

## Screenshot

Picker خودش Screenshot نمی‌گیرد (مرورگر بدون مجوز صفحه‌ضبط نمی‌تواند از خودش عکس بگیرد).
به‌جای آن Claude Code از **Playwright MCP** استفاده می‌کند: صفحه را باز می‌کند،
با `element.cssPath` همان عنصر را می‌گیرد و قبل/بعد از تغییر عکس می‌گیرد.

## اگر Pick Server اجرا نباشد

Picker همان اطلاعات را در Clipboard می‌گذارد؛ کافی است در Claude Code `Paste` کنی.
هیچ چیزی از دست نمی‌رود، فقط `/ui-pick` بدون فایل کار نمی‌کند.

## امنیت

- Pick server فقط روی `127.0.0.1` گوش می‌دهد.
- فقط Originهای `localhost:5000` / `127.0.0.1:5000` / `localhost:5001` پذیرفته می‌شوند
  (قابل افزایش با `PTG_UI_PICK_ORIGIN`).
- حجم payload حداکثر ۲ مگابایت؛ فقط فایل می‌نویسد، هیچ دستوری اجرا نمی‌کند.
- `.ptg-ui-pick/` در `.gitignore` است.
- هیچ تنظیم امنیتی مرورگر غیرفعال نشده است.
