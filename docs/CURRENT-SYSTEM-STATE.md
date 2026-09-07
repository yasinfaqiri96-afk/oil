# CURRENT SYSTEM STATE

> آخرین به‌روزرسانی: ۲۰۲۶-۰۷-۱۳
> این تنها سند «وضعیت» پروژه است. گزارش‌های فازی و batchهای قدیمی حذف شده‌اند؛ آنچه باقی مانده در Git history است.

## خلاصه در یک نگاه

| محور | وضعیت |
|---|---|
| معماری | ASP.NET Core MVC .NET 8 + EF Core 8 + PostgreSQL — [ARCHITECTURE.md](ARCHITECTURE.md) |
| دامنه | ۶۹ DbSet، ۷۴ migration — [DOMAIN-MODEL.md](DOMAIN-MODEL.md) |
| UI | Design System `ak-*` (مهاجرت کامل) — [UI-DESIGN-SYSTEM.md](UI-DESIGN-SYSTEM.md) |
| تست | ۸۸۳ pass / ۱۶ fail / ۸۹۹ — [TESTING.md](TESTING.md) |
| Build | `0 error` / ۱ warning قدیمی (`EF1002`) |
| اجرا | [DEPLOYMENT.md](DEPLOYMENT.md) |

## ماژول‌های فعال و قابل استفاده

**Master Data** — Products, Companies, Suppliers, Customers, ServiceProviders, Partners, Terminals, StorageTanks, Locations, ExpenseTypes, Vessels, Trucks, Wagons, Drivers, Employees, Currencies, Units, CashAccounts, Sarrafs, OperationalAssets

**قرارداد و قیمت** — Contracts, ContractAmendments (immutable), ContractPricingRules, PlattsRates, DailyFxRates, `PricingService` (قیمت‌گذاری بر اساس تاریخ تراکنش + fallback)

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
| ۱۶ تست قرمز | بدهی قدیمی: freight/rent در Loading، RUB/صراف در Suppliers، EditPricing، capacity در InventoryTransportBatchService و چند marker. فهرست دقیق در [TESTING.md](TESTING.md). |
| کنترلرهای بزرگ | `LoadingController` و `ContractJourneyController` منطق زیادی درون خود دارند؛ extraction به service layer انجام نشده. |
| CSS بدون minify/bundle | فایل‌ها خام سرو می‌شوند. gzip/brotli سرور تنها فشرده‌سازی فعلی است. |
| بلوک‌های مردهٔ CSS | داخل فایل‌های mixed (`09-pages`, `13-compat`, `14-master-details`) هنوز قواعد بی‌مصرف هست؛ حذف surgical ریسک brace-imbalance دارد. |
| PostgreSQL integration tests | وجود ندارد؛ semantics تراکنش و locking روی provider واقعی تست نشده. |
| permission ریزدانه | فقط policy سطح ماژول (`ManageData`…)؛ permission در سطح action/field نیست. |
| pagination / performance | queryهای بزرگ و گزارش‌ها hardening نشده‌اند. |
| soft delete / archive | سیاست عمومی ندارد؛ حذف سخت + `AuditLog`. |
| rate limit / lockout | برای login وجود ندارد. |
| `restart-server.bat` | حذف شد (به فایل ناموجود `run-system.bat` ارجاع می‌داد). برای اجرا از `run-dev.bat` استفاده کنید. |

## اولویت بعدی

1. PostgreSQL integration tests + تست سناریوهای واقعی workbook/Excel.
2. رفع ۱۶ تست قرمز (شروع از freight/rent در Loading).
3. security hardening: rate limit ورود، permission ریزدانه.
4. performance: pagination گزارش‌ها، minify/bundle دارایی‌های static.
5. extraction کنترلرهای بزرگ به service layer.

## قاعدهٔ مرجع‌بودن

منبع حقیقت فقط کد داخل `src/` و همین پوشهٔ `docs/` است. هیچ خروجی build، artifact یا بکاپ مبنای تصمیم‌گیری نیست.
