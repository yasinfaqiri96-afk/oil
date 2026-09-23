# TESTING

## پروژهٔ تست

`tests/PTGOilSystem.Web.Tests` — xUnit، ۶۸ فایل تست.

سه دستهٔ اصلی:

| دسته | نمونه | چه چیزی را قفل می‌کند |
|---|---|---|
| Controller / منطق تجاری | `LoadingControllerTests`, `SuppliersControllerTests`, `ShipmentPnlControllerTests` | محاسبات کرایه، کسری، FX، تخصیص، Ledger |
| ساختار View | `ShellViewStructureTests`, `ContractJourneyViewStructureTests` | markerهای UI و قرارداد کامپوننت‌های مشترک |
| ساختار پشتیبان | `MasterDataCleanupTests`, `UnitConversionSupportStructureTests` | ایمنی حذف master data، تبدیل واحد |

## اجرا

```powershell
.\scripts\test-fast.ps1 -Build
.\scripts\test-fast.ps1
.\scripts\test-full.ps1

# اجرای مستقیم پس از build موفق موجود
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-build --no-restore
```

> اسکریپت‌ها build موجود را reuse می‌کنند. پس از تغییر source فقط یک‌بار `-Build` بدهید؛
> اجرای مستقیم تست همیشه باید با `--no-build --no-restore` باشد.

## Baseline فعلی

```text
883 pass / 16 fail / 0 skipped / 899 total
```

**قاعده: هر تغییر باید Total را ثابت نگه دارد و شکست جدید = صفر.** تست‌ها را Skip یا حذف نکنید؛ اگر ساختار View عوض شد، همان تست را به قرارداد جدید منتقل کنید (نه تضعیف).

### ۱۶ شکست شناخته‌شده (بدهی قدیمی، خارج از دامنهٔ UI)

| ناحیه | تعداد |
|---|---|
| `LoadingControllerTests` — freight / rent | ۶ |
| `SuppliersControllerTests` — RUB / صراف | ۲ |
| `EditPricing` | ۲ |
| `ContractJourney` — تخصیص مصارف | ۱ |
| `Roles_Create` | ۱ |
| `AuditLogs` Index | ۱ |
| `Sarrafs/Details` view | ۱ |
| `Sidebar` | ۱ |
| `InventoryTransportBatchService` capacity | ۱ |

هیچ‌کدام از مهاجرت UI ناشی نشده‌اند؛ در HEAD تمیز هم قرمزند.

## تست ساختار View — چطور کار می‌کند

`ShellViewStructureTests` وجود markerهای canonical (مثل `ak-form-page`, `ak-list-page`, `.ak-row-menu`, `.ak-form-grid`) را در فایل‌های Razor assert می‌کند. اگر markup صفحه‌ای را عوض کردید، **هم‌زمان** marker تست را به‌روز کنید. تغییر ساختار مجاز است؛ ضعیف‌کردن منطق تست نیست.

## قواعد اعتبارسنجی قبل از تحویل

1. `git diff --check` پاس (warning بی‌ضرر `LF→CRLF` قابل قبول است).
2. Web build: `0 error` (۱ warning قدیمی `EF1002` در `MaintenanceController` پذیرفته‌شده است).
3. Full test: Total ثابت (۸۹۹)، شکست جدید صفر.
4. برای CSS: توازن brace هر فایل ویرایش‌شده را بررسی کنید.
5. برای JS: `node --check <file>`.

## آنچه هنوز نداریم

- PostgreSQL integration tests روی provider واقعی (transaction semantics، locking).
- تست‌های end-to-end مرورگری (Playwright فقط برای QA بصری دستی استفاده شده).
