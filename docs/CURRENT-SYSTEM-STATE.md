# CURRENT SYSTEM STATE

> آخرین به‌روزرسانی مستندات مالی: ۲۰۲۶-۱۰-۱۰
> آمار Baseline زیر مربوط به شروع مأموریت اصلاح مشعل است، نه ادعای قبولی نسخهٔ اصلاح‌شده. نتیجهٔ Build، تست و آمادگی نشر باید از گزارش همان مرحله خوانده شود. تغییرات مالی: [FINANCIAL-WORKFLOWS.md](FINANCIAL-WORKFLOWS.md).

## خلاصه در یک نگاه

| محور | وضعیت |
|---|---|
| معماری | ASP.NET Core MVC .NET 8 + EF Core 8 + PostgreSQL — [ARCHITECTURE.md](ARCHITECTURE.md) |
| دامنه | Entityها، DbSetها و migrationهای سورس جاری مرجع‌اند؛ تعدادهای قدیمی ۶۹/۷۴ به‌روز نیستند — [DOMAIN-MODEL.md](DOMAIN-MODEL.md) |
| UI | Design System `ak-*` (مهاجرت کامل) — [UI-DESIGN-SYSTEM.md](UI-DESIGN-SYSTEM.md) |
| تست | Baseline مأموریت: ۳٬۹۴۷ اجرا، ۳٬۹۲۱ موفق، ۲۶ ناموفق و صفر Skip. این آمار نتیجهٔ اصلاحات بعدی نیست — [TESTING.md](TESTING.md) |
| Build | Build اولیه موفق بود؛ وضعیت هر مرحله با خروجی همان Build و تست تأیید می‌شود. |
| اجرا | [DEPLOYMENT.md](DEPLOYMENT.md) |

## ماژول‌های فعال و قابل استفاده

**Master Data** — Products, Companies, Suppliers, Customers, ServiceProviders, Partners, Terminals, StorageTanks, Locations, ExpenseTypes, Vessels, Trucks, Wagons, Drivers, Employees, Currencies, Units, CashAccounts, Sarrafs, OperationalAssets

**قرارداد و قیمت** — Contracts, ContractAmendments (immutable), ContractPricingRules, PlattsRates, DailyFxRates و `PricingService`. در روش Platts، نرخ مرجع جای قیمت نهایی قطعی دستی را نمی‌گیرد. قواعد مالی و عملیات گروهی: [FINANCIAL-WORKFLOWS.md](FINANCIAL-WORKFLOWS.md).

**عملیات** — Loading, LoadingReceipts, Inventory, InventoryTransportLegs / Receipts, Dispatch, Shipments, ShipmentContracts, TruckSettlements, LossEvents, CustomsDeclarations, InventoryReports/IlinkaStock

**فروش و مصرف** — Sales (تکی + گروهی `SalesBatch`)، Expenses (تکی + گروهی `ExpenseBatch`)، ExpenseRules + `ExpenseRuleEngine`

**مالی** — Ledger, Payments, Balance, AccountStatements, ContractBalanceTransfers, SarrafSettlements, ThreeWaySettlement, Reconciliation, ShipmentPnl, Reports (+ export CSV در همهٔ اینها)

**سیستم** — Users, Roles, AuditLogs, ContractJourney (پروندهٔ کامل قرارداد با ۱۰ تب Ajax)، Invoices (چاپ Faisal / Fawad)

## کاری که به‌تازگی تمام شده

**مهاجرت UI به Design System `ak-*`** — کل صفحات از چند دیزاین‌سیستم موازی (`sd-*`, `od-*`, `pp-*`, `cj-*`, `ulist-*`, `ds-*`) به یک قرارداد واحد منتقل شدند.

| معیار | قبل | بعد |
|---|---|---|
| CSS در هر page-load | ۱٬۲۸۹ KB / ۳۶ فایل | **۴۰۸ KB / ۲۶ فایل** |
| تعداد rule CSS | ۶٬۲۱۰ | **۲٬۲۶۲** |
| `!important` | ۴٬۱۱۱ | **۸۸۳** |
| خطوط کد | — | **−۳۸٬۵۴۸ خالص** |

جزئیات Design System فعال: [UI-DESIGN-SYSTEM.md](UI-DESIGN-SYSTEM.md).
Backend، DB و queryها در این مهاجرت **تغییر نکردند**.

## بدهی فنی شناخته‌شده

| مورد | توضیح |
|---|---|
| تست‌های ناموفق Baseline | ۲۶ مورد در اجرای اولیه: از جمله fixture اکسل `Payment.xlsx` ناموجود، انتظارهای متن/ساختار قدیمی و موارد قابل بررسی. هیچ failure صرفاً برای سبزشدن تست حذف نمی‌شود؛ نتیجهٔ رفع هر مورد در گزارش مأموریت ثبت شود. |
| کنترلرهای بزرگ | `LoadingController` و `ContractJourneyController` منطق زیادی درون خود دارند؛ extraction به service layer انجام نشده. |
| CSS بدون minify/bundle | فایل‌ها خام سرو می‌شوند. gzip/brotli سرور تنها فشرده‌سازی فعلی است. |
| بلوک‌های مردهٔ CSS | داخل فایل‌های mixed (`09-pages`, `13-compat`, `14-master-details`) هنوز قواعد بی‌مصرف هست؛ حذف surgical ریسک brace-imbalance دارد. |
| PostgreSQL integration tests | اکنون fixtureهای دیتابیس موقت واقعی برای accounting، همزمانی و تبدیل انبوه وجود دارند. نتیجهٔ هر اجرا باید از گزارش همان اجرا خوانده شود؛ وضعیت قدیمی «وجود ندارد» معتبر نیست. |
| permission ریزدانه | فقط policy سطح ماژول (`ManageData`…)؛ permission در سطح action/field نیست. |
| pagination / performance | queryهای بزرگ و گزارش‌ها hardening نشده‌اند. |
| soft delete / archive | سیاست عمومی ندارد؛ حذف سخت + `AuditLog`. |
| rate limit / lockout | Rate limiter در `Program.cs` ثبت و فعال است. سیاست دقیق هر endpoint و تست‌های امنیتی مرجع وضعیت جاری‌اند؛ عبارت قدیمی «وجود ندارد» معتبر نیست. |
| `restart-server.bat` | حذف شد (به فایل ناموجود `run-system.bat` ارجاع می‌داد). برای اجرا از `run-dev.bat` استفاده کنید. |

## اولویت بعدی

1. توسعهٔ PostgreSQL integration tests و بررسی سناریوهای واقعی workbook/Excel روی دیتابیس مستقل.
2. رفع علت‌های تأییدشدهٔ تست‌های ناموفق Baseline و تکرار Regression پس از اصلاح.
3. security hardening: rate limit ورود، permission ریزدانه.
4. performance: pagination گزارش‌ها، minify/bundle دارایی‌های static.
5. extraction کنترلرهای بزرگ به service layer.

## قاعدهٔ مرجع‌بودن

منبع حقیقت فقط کد داخل `src/` و همین پوشهٔ `docs/` است. هیچ خروجی build، artifact یا بکاپ مبنای تصمیم‌گیری نیست.
