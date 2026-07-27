# Repository Guidelines

## دامنه و معماری

- با کاربر فارسی/دری صحبت کن، مگر خودش انگلیسی بخواهد؛ پاسخ نهایی کوتاه و عملی باشد.
- برنامهٔ اصلی `src/PTGOilSystem.Web/` است: ASP.NET Core MVC/.NET 8، EF Core/PostgreSQL، Razor و Bootstrap RTL. تست‌ها در `tests/PTGOilSystem.Web.Tests/` با xUnit هستند. build عادی را روی Web project بگیر؛ solution شامل Desktop و ابزارهای نگهداری دیتابیس نیز هست.
- مسیر معمول: `Controller → Service → ApplicationDbContext`. منطق چندمرحله‌ای، مالی و ثبت Ledger باید در Service بماند. برای UI از design system موجود `ak-*` و `docs/UI-DESIGN-SYSTEM.md` پیروی کن.
- برای پرسش‌های گستردهٔ کد، اگر `graphify-out/graph.json` موجود بود ابتدا `graphify query "<question>"` اجرا کن؛ پس از تغییر کد `graphify update .` را اجرا کن.

## مرزهای غیرقابل حدس

- بدون درخواست صریح، Entity، DbContext، Migration/schema، `StockService`، `PricingService`، Ledger/P&L، Payment/CashAccount، InventoryMovement/Allocation و محاسبات پول، وزن، نرخ یا FX را تغییر نده. اگر لازم شد، اثر تغییر را توضیح بده و اجازه بگیر.
- موجودی فقط از `InventoryMovement` و `StockService` می‌آید. `DirectSale` حرکت جعلی نمی‌سازد؛ `DirectDispatchFromReceipt` نباید StockService را صدا بزند. `ContractJourney` فقط read-only/navigation است و stock، ledger یا payment نمی‌سازد.
- همهٔ POST formها باید `@Html.AntiForgeryToken()` صریح داشته باشند. هیچ فیلد backend را برای ساده‌سازی UI حذف یا مخفی نکن؛ فیلد کم‌استفاده را به Advanced منتقل کن.

## اجرا و بررسی

```powershell
.\scripts\run-local.ps1                 # اجرا روی http://localhost:5000
.\scripts\run-local.ps1 -Watch          # hot reload؛ معادل run-dev.bat
.\scripts\run-local.ps1 -ApplyMigrations # فقط با درخواست صریح
dotnet build src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --no-restore # پس از restore اولیه
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-build --filter "FullyQualifiedName~ClassName.MethodName"
.\scripts\test-fast.ps1
.\scripts\test-full.ps1
.\scripts\test-accounting.ps1
```

- startup به PostgreSQL واقعی نیاز دارد؛ InMemory fallback وجود ندارد. runner مهاجرت خودکار را خاموش می‌کند، اما اجرای مستقیم app به‌صورت پیش‌فرض migration اجرا می‌کند؛ برای کار محلی runner را ترجیح بده.
- `npm test` عمداً شکست می‌خورد؛ تست واقعی با `dotnet test` یا `scripts/test-*.ps1` است.
- تست‌های accounting روی PostgreSQL دیتابیس موقت می‌سازند و با `DROP DATABASE ... FORCE` حذف می‌کنند؛ فقط روی محیط تست اجرا شوند.
- برای UI-only چند تغییر را batch کن و فقط در پایان Web build بگیر؛ full test و EF pending-model check لازم نیست. برای Controller/ViewModel، Web build و تست همان کلاس کافی است. فقط تغییر Entity/DbContext/Migration نیازمند solution build، full test و `dotnet ef migrations has-pending-model-changes --project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj` است.
- تغییر markup ممکن است نیازمند به‌روزرسانی `ShellViewStructureTests` یا تست ساختاری همان صفحه باشد؛ قرارداد تست را ضعیف نکن.

## قرارداد مخزن

- `.editorconfig`: UTF-8، LF، final newline و بدون trailing whitespace.
