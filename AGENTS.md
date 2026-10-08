# Repository Guidelines

- مالکیت compilation: `Web → Migrations → Persistence` و `Web → Persistence`؛ migrationها در `src/PTGOilSystem.Migrations/Migrations/` هستند. Entity/DbContext در مسیر و namespace قبلی با linked source کامپایل می‌شوند. برای کار روزمره `dotnet build Web --no-restore` با project references فعال؛ از `Rebuild` و `BuildProjectReferences=false` استفاده نکن. دستور EF باید `--project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj` داشته باشد؛ پس از build تازه `--no-build`. توضیح: `docs/migrations-project.md`.

## دامنه و معماری

- با کاربر فارسی/دری صحبت کن، مگر خودش انگلیسی بخواهد؛ پاسخ نهایی کوتاه و عملی باشد.
- برنامهٔ اصلی `src/PTGOilSystem.Web/` است: ASP.NET Core MVC/.NET 8، EF Core/PostgreSQL، Razor و Bootstrap RTL. تست‌ها در `tests/PTGOilSystem.Web.Tests/` با xUnit هستند. build عادی را روی Web project بگیر؛ solution شامل Desktop و ابزارهای نگهداری دیتابیس نیز هست.
- مسیر معمول: `Controller → Service → ApplicationDbContext`. منطق چندمرحله‌ای، مالی و ثبت Ledger باید در Service بماند. برای UI از design system موجود `ak-*` و `docs/UI-DESIGN-SYSTEM.md` پیروی کن.
- ابتدا فایل و متد مربوط را با `rg` مستقیم پیدا کن؛ Graphify پیش‌نیاز بررسی کد نیست. فقط با درخواست صریح کاربر، تغییر معماری بزرگ، تغییر مهم روابط dependency/service، یا در پایان feature بزرگ و فقط یک بارِ لازم اجرا شود. برای bug fix کوچک، UI، CSS/JS/View، Controller کوچک، تغییر متن و اجرای دوبارهٔ تست، Graphify اجرا نشود. این قاعده بر دستورهای عمومی skillهای Graphify اولویت دارد.

## سرعت توسعه و context

- قاعدهٔ کار: `inspect once → batch edits → validate once`. ابتدا وضعیت اولیه و فایل‌های مرتبط را بررسی کن؛ پس از یافتن root cause، exploration را متوقف و implementation را شروع کن.
- برای تسک عادی کل repository، generated files، `bin/obj` یا migrationها را نخوان. در فایل‌های بزرگ مثل `LoadingController.cs`، `SalesController.cs` و `Create.cshtml` از search symbol/text، محدودهٔ متد و خطوط اطراف استفاده کن؛ کل فایل فقط با ضرورت مشخص خوانده شود.
- فایل بدون تغییر را بی‌دلیل دوباره نخوان. برای تسک ساده subagent، research اضافی یا plan طولانی نساز.
- بعد از هر edit، build، test یا `git diff/status` تکرار نشود؛ تغییرها و validation را batch کن. تکرار فقط پس از تغییر تازه، شکست check یا نگرانی مشخص مجاز است.
- دو agent هم‌زمان روی checkout و فایل‌های مشترک تغییر ندهند؛ Claude/Codex هم‌زمان باید Git worktree یا checkout جدا با branch مناسب داشته باشند. branch جدا در همان checkout جداسازی ایجاد نمی‌کند.

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
# build تازهٔ Web و تست‌ها در یک dependency graph؛ سپس targeted test بدون rebuild:
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~ClassName.MethodName'
.\scripts\dev-verify.ps1 -Ui -Paths src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css
.\scripts\dev-verify.ps1 -Full # فقط برای تغییرهای پرریسک/مشترک یا release
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ClassName.MethodName"
.\scripts\test-fast.ps1
.\scripts\test-full.ps1
.\scripts\test-accounting.ps1
```

- startup به PostgreSQL واقعی نیاز دارد؛ InMemory fallback وجود ندارد. runner مهاجرت خودکار را خاموش می‌کند، اما اجرای مستقیم app به‌صورت پیش‌فرض migration اجرا می‌کند؛ برای کار محلی runner را ترجیح بده.
- `npm test` عمداً شکست می‌خورد؛ تست واقعی با `dotnet test` یا `scripts/test-*.ps1` است.
- تست‌های accounting روی PostgreSQL دیتابیس موقت می‌سازند و با `DROP DATABASE ... FORCE` حذف می‌کنند؛ فقط روی محیط تست اجرا شوند.
- برای CSS/JS/متن خالص validation سبک با `dev-verify.ps1 -Ui -Paths ...` کافی است؛ تغییر Razor نیازمند یک Web build برای حفظ بررسی کامپایل است. UI هرگز full test، solution build یا EF check لازم ندارد.
- برای اصلاح معمول Web، solution build ممنوع است؛ فقط Web و در صورت نیاز build تازهٔ پروژهٔ تست با dependencyهایش. Controller/ViewModel فقط تست همان کلاس؛ Service فقط تست همان Service؛ UI فقط تست ساختاری مرتبط در صورت وجود.
- اگر تست لازم است، `dev-verify.ps1 -Web -Filter ...` پروژهٔ تست را یک بار build می‌کند؛ Web نیز در همان graph فقط یک بار ساخته می‌شود. سپس تمام تست‌های هدفمند با `--no-build --no-restore` اجرا شوند. Web build به‌تنهایی تضمین نمی‌کند DLL تست تازه باشد.
- full test فقط برای accounting، inventory، ledger، migration، cross-cutting change یا release/deploy verification مجاز است. solution build فقط برای shared project، تغییر Entity/Migration architecture یا release validation مجاز است.
- پس از restore اولیه، تمام فرمان‌های development از `--no-restore` استفاده کنند؛ restore فقط پس از تغییر dependency/project/build imports، نبود assets یا خطای مشخص assets لازم است. اسکریپت‌ها restore پنهان ندارند.
- تغییر Entity/DbContext/Migration نیازمند validation کامل‌تر و EF pending-model check با `--no-build` پس از build تازه است؛ هیچ check مجوز apply migration نیست.
- Debug build در محیط Production bundle نمی‌سازد؛ برای اجرای Debug خارج Development از `-p:PtgBuildBundles=true` استفاده کن. Release و Publish bundle می‌سازند. جزئیات workflow و اندازه‌گیری: `docs/development-performance.md`.
- تغییر markup ممکن است نیازمند به‌روزرسانی `ShellViewStructureTests` یا تست ساختاری همان صفحه باشد؛ قرارداد تست را ضعیف نکن.

## قرارداد مخزن

- `.editorconfig`: UTF-8، LF، final newline و بدون trailing whitespace.
