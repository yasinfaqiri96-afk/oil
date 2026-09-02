# PTG Oil System — گزارش شبیه‌سازی ۱۲ ماه بهره‌برداری واقعی

**تاریخ:** 2026-08-29
**دامنه:** Investigation + Simulation. **هیچ باگی در این مرحله اصلاح نشده است.**
**ایمنی:** هیچ عملیاتی روی دیتابیس Production انجام نشد. همهٔ تست‌ها روی دیتابیس‌های موقت با
پیشوند اجباری `ptg_oil_accounting_test_` ساخته و در پایان حذف شدند
(نگهبان: `src/PTGOilSystem.Web/Data/DatabaseSafetyGuard.cs`). تنها دسترسی به `ptg_oil_system`
یک `SELECT` روی `information_schema` برای خواندن ستون‌های NOT NULL بود.

---

## ۰. خلاصهٔ مدیریتی

بعد از ۱۲ ماه کار روزانه، **اول چیزی که خراب می‌شود «موجودی مخزن» و «تعداد اسناد مالی» است، نه سرعت سیستم.**

سه ریسک قطعی و بازتولیدشده:

| # | چه چیزی می‌شکند | چرا | وضعیت |
|---|---|---|---|
| ۱ | مصرف/هزینه دوبار ثبت می‌شود و دوبار در دفتر کل می‌نشیند | فرم مصرف محافظ Idempotency ندارد (فقط ۴ فرم دارند) | **CONFIRMED** |
| ۲ | موجودی مخزن منفی می‌شود | فروشِ با تاریخ گذشته فقط با موجودیِ «همان تاریخ» چک می‌شود و نگهبان منفی‌شدنِ آینده عمداً خاموش است | **CONFIRMED** |
| ۳ | سهم سود شرکا در گذشته بازنویسی می‌شود | درصد سهم Snapshot نمی‌شود و صورت‌حساب همیشه از سطر زندهٔ `ContractPartner` می‌خواند | **CONFIRMED** |

خبر خوب، که هم اندازه‌گیری شد: **قفل هم‌زمانی مخزن، Idempotency فروش/پرداخت/قرارداد، تطبیق ماهانهٔ
لجر با اسناد، و کارایی صفحات اصلی سالم‌اند.** سیستم روی ۳۰۰٬۰۰۰ سطر دفتر کل هنوز سریع است.

---

## PHASE 1 — نقشهٔ سیستم و «منبع حقیقت» هر بخش

### ۱.۱ ابعاد کد

- ۱۱۲ Controller، ~۹۰ Service، ۱۷ فایل Entity، `ApplicationDbContext` با ۲۲۵۵ خط، ۱۰۷ Migration.
- ۴۹ Unique Index، ۲۰۷ رابطهٔ `DeleteBehavior.Restrict` در برابر تنها ۱۲ `Cascade`
  (Cascadeها فقط روی سطرهای فرزند/جزئیات‌اند، نه اسناد مالی).
- ۵۹۴ ایندکس در schema عمومی.

### ۱.۲ منبع حقیقت (Source of Truth)

| حوزه | منبع قطعی | نکته |
|---|---|---|
| موجودی فیزیکی | `InventoryMovements` (جمع علامت‌دار) | `StockService` فقط همین را می‌خواند؛ هیچ ستون «مانده» ذخیره‌شده‌ای وجود ندارد. تأیید شد که ریاضی و سرویس دقیقاً یکی هستند. |
| بدهی/طلب طرف‌حساب | `LedgerEntries` با کلید `(SourceType, SourceId)` | **هیچ FK وجود ندارد** — فقط قرارداد نام‌گذاری. |
| بهای خرید | `LoadingRegister.LoadingPriceUsd` + قرارداد | قیمت قطعی‌شده با ویرایش قرارداد دوباره محاسبه **نمی‌شود** (رفتار درست). |
| درآمد | `SalesTransactions.TotalUsd` | ویرایش فروش فقط «یادداشت» را اجازه می‌دهد. |
| هزینه | `ExpenseTransactions.AmountUsd` | ویرایش، سطر لجر را در جای خودش به‌روز می‌کند (بدون تکرار). |
| مانده نقدی | `PaymentTransactions` + `LedgerEntry` یک‌به‌یک (`PaymentTransactions.LedgerEntryId` یکتا) | |
| مفاد شراکت | **محاسبهٔ زنده** از `ContractPartner.SharePercent` | هیچ Snapshot تاریخی نیست. |
| P&L شرکت | `ProfitAndLossService` روی فروش/مصرف/تسویهٔ صراف | |
| دفتر دوطرفهٔ جدید (Journal/Account/FiscalPeriod) | **غیرفعال** (`Accounting:Enabled = false` و همهٔ Pilotها false) | `PeriodGuard` فقط به این ماژول وصل است. |

### ۱.۳ نکتهٔ معماری مهم

نوشتن در دفتر کل در **~۱۸ نقطهٔ مختلف** انجام می‌شود (`PaymentsController`، `ExpensesController`،
`LoadingController`، `SalesController(.Group)`، `DispatchController`، `SarrafSettlementService`،
`SupplierPaymentAllocationService`، `SupplierBalanceTransferService`، `InventoryTransportReceiptService`،
`AssetRentPostingService`، `ExpenseRuleEngine`، `EmployeeSalaryService`، `ContractBalanceTransferService`،
`LedgerReversalWriter`، `DispatchFreightExpenseSync`، `ThreeWaySettlementController`،
`InventoryTransportLegsController`، `SalesController.Group`). هیچ «Posting Service» واحدی وجود ندارد.

---

## PHASE 2 — Harness شبیه‌سازی که ساخته شد

فایل‌های جدید (فقط تست، هیچ کد Production تغییر نکرد):

```
tests/PTGOilSystem.Web.Tests/Simulation/
  SimulationPostgresFixture.cs          دیتابیس موقت + Migration + Drop امن
  SimulationWorld.cs                    داده deterministic ۱۲ ماهه (بذر ثابت 20260101)
  SimulationFindings.cs                 جمع‌کنندهٔ یافته‌ها + خروجی markdown
  TwelveMonthProductionSimulationTests.cs   ۱۲ ماه + تطبیق‌های ماهانه + کارایی
  ScaleAndPerformanceTests.cs           حجم چندساله (۳۰۰k لجر) + بودجهٔ زمانی
  ProductionRiskProbeTests.cs           ۹ سناریوی واقعی روی مسیرِ واقعیِ نوشتن
```

### اجرا

```bash
# اختیاری: اگر رمز postgres با PTG_LOCAL_DB_PASSWORD فرق دارد
export PTG_TEST_POSTGRES_ADMIN="Host=localhost;Port=5432;Username=postgres;Password=...;Database=postgres"
export PTG_SIM_REPORT_DIR="<مسیر خروجی گزارش>"

dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj \
  --filter "FullyQualifiedName~Simulation" --logger "console;verbosity=detailed"
```

اگر PostgreSQL در دسترس نباشد، fixture خودش را «ناموجود» علامت می‌زند و تست‌ها Skip می‌شوند
(بدون شکست کاذب).

### حجم دادهٔ ساخته‌شده (اندازه‌گیری‌شده)

| | مقدار |
|---|---|
| Contracts | ۸۰ (۳۰ خرید شخصی، ۳۰ خرید شراکتی ۵۰/۵۰، ۲۰ فروش) |
| LoadingRegisters / LoadingReceipts | ۱۲۰۰ / ۱۲۰۰ |
| InventoryMovements | ۲۸۲۰ |
| SalesTransactions | ۱۵۰۰ |
| ExpenseTransactions | ۱۵۰۰ |
| PaymentTransactions | ۱۵۰۰ (۲۰٪ تأمین مالی شریک) |
| TruckDispatches | ۶۰۰ |
| LossEvents | ۱۲۰ |
| LedgerEntries | ۵۷۰۰ |
| زمان ساخت | ۲۷.۵ ثانیه |
| مقیاس دوم (`ScaleAndPerformanceTests`) | ۳۰۰٬۰۰۰ لجر / ۱۵۰٬۰۰۰ حرکت موجودی / ۶۰٬۰۰۰ فروش / ۶۰٬۰۰۰ مصرف / ۶۰٬۰۰۰ پرداخت |

> **صداقت روش:** دادهٔ حجیم برای Invariantها و کارایی، مستقیم و با همان شکلی که Controllerهای
> واقعی می‌سازند نوشته شد (فروش ⇒ حرکت خروجی + لجر `Sale`؛ مصرف ⇒ لجر `Expense`؛ …).
> **باگ‌های رفتاری همگی از مسیر واقعیِ Controller بازتولید شده‌اند**، نه از دادهٔ ساختگی.
> در نتیجه، دیپ‌های موجودیِ منفی که در دادهٔ حجیم دیده شد **artifact مولد داده است، نه باگ برنامه**
> — مدرکِ واقعیِ منفی‌شدن موجودی، Probe 03 است که از `SalesController` عبور می‌کند.

---

## ۳. نتیجهٔ تطبیق‌های ۱۲ ماهه (اندازه‌گیری‌شده)

| بررسی | نتیجه |
|---|---|
| `StockService` در برابر ریاضیِ حرکات موجودی — ۶۰ scope | ✅ اختلاف صفر |
| ردیف لجر یتیم (Sale/Expense/Loading) | ✅ صفر |
| سندِ بدون ردیف لجر (فروش/مصرف/پرداخت) | ✅ صفر |
| لجر تکراری برای یک فروش | ✅ صفر |
| جمع ماهانهٔ فروش/مصرف در برابر جمع لجر — هر ۱۲ ماه | ✅ منطبق |
| جمع سهم مفاد شرکا در برابر مفاد دفترِ قرارداد — ۴ جفت شریک | ✅ منطبق |
| ماندهٔ ۶ تأمین‌کننده = بارگیری − پرداخت | ✅ منطبق |

**یعنی: تا وقتی داده از مسیر درست وارد شود، حساب‌ها می‌خوانند. شکست از «مسیر ورود» می‌آید، نه از «ریاضی».**

## ۴. کارایی (اندازه‌گیری‌شده)

روی ۵۷۰۰ سطر لجر (یک سال):

| مسیر | زمان |
|---|---|
| `StockService.GetMovementSummaryAsync` (کل تاریخ) | ۷۸ ms |
| `ProfitAndLossService.BuildCompanyAsync` | ۱۲۱ ms |
| `NegativeStockAnalysisService.AnalyzeAsync` | ۶۴ ms |
| صفحهٔ اول دفتر کل (۵۰ ردیف) | ۸ ms |
| `PartnershipStatementService.BuildAsync` | ۲۲ ms |

روی ۳۰۰٬۰۰۰ سطر لجر / ۱۵۰٬۰۰۰ حرکت موجودی (سه سال پرحجم):

| مسیر | زمان | بودجه |
|---|---|---|
| صفحهٔ اول دفتر کل | ۵۴ ms | ۱۰۰۰ |
| صفحهٔ عمیق دفتر کل (offset 250,000) | ۳۵۷ ms | ۳۰۰۰ |
| شمارش کل دفتر کل | ۱۵۴ ms | ۲۰۰۰ |
| **گردش حساب مشتری، بدون فیلتر تاریخ** | **۳٬۹۷۸ ms** | ۴۰۰۰ |
| گردش حساب تأمین‌کننده، بدون فیلتر تاریخ | ۱٬۹۴۹ ms | ۴۰۰۰ |
| موجودی آزاد یک مخزن | ۱۲۵ ms | ۱۵۰۰ |
| خلاصهٔ حرکات موجودی (کل تاریخ) | ۵۴۲ ms | ۵۰۰۰ |
| تحلیل موجودی منفی (کل تاریخ) | ۱٬۹۶۷ ms | ۸۰۰۰ |
| P&L شرکت (کل تاریخ) | ۲۵۱ ms | ۸۰۰۰ |
| لیست فروش صفحهٔ اول با join | ۳۶ ms | ۱۵۰۰ |

**تنها مسیر نزدیک به مرز: گردش حساب طرف‌حساب** — تمام تاریخچه بدون صفحه‌بندی به حافظه می‌آید.

---

## ۵. فهرست باگ‌ها

### P0 — خرابی مالی / خرابی داده

---

#### PTG-P0-01 — ثبت دوبارهٔ مصرف، دو سند و دو ردیف دفتر کل می‌سازد
- **Severity:** P0 · **Module:** Expenses / Ledger · **وضعیت:** **CONFIRMED**
- **سناریو:** اینترنت ضعیف. کاربر «ثبت مصرف» می‌زند، پاسخ نمی‌آید، صفحه را Refresh می‌کند و مرورگر POST را دوباره می‌فرستد. (یا: دو تب، یا Timeout و تلاش دوباره.)
- **مراحل بازتولید:** `Probe01_Double_Submit_Of_Expense_Creates_Duplicate_Expense_And_Duplicate_Ledger`
  — دو بار `ExpensesController.Create` با همان مدل (۱۲٬۵۰۰ USD).
- **انتظار:** یک سند مصرف، یک ردیف لجر.
- **واقعیت:** `expenses=2 ledgerRows=2 totalUsd=25,000.00` — دقیقاً دو برابر.
- **علت ریشه‌ای:** `IFormTokenGuard` فقط در ۴ فرم استفاده می‌شود
  (`Views/Contracts/Create.cshtml`، `Views/Payments/Create.cshtml`، `Views/Sales/Create.cshtml`،
  `Views/InventoryTransportLegs/CreateFromInventory.cshtml`). مسیر مصرف اصلاً `formToken` نمی‌گیرد.
  محافظ سمت مرورگر (`wwwroot/js/core.js` → *Double-submit guard*) فقط کلیک دوم روی همان صفحه را
  می‌گیرد؛ Refresh/Retry/تب دوم را نمی‌گیرد.
- **فایل/متد:** `src/PTGOilSystem.Web/Controllers/ExpensesController.cs:846` `Create(ExpenseCreateViewModel)`
  · `src/PTGOilSystem.Web/Services/FormTokenGuard.cs`
- **اثر روی دیتابیس:** سند مصرف تکراری + `LedgerEntry` تکراری با `SourceType="Expense"` و `SourceId` متفاوت.
- **اثر مالی:** هزینهٔ قرارداد و P&L و ماندهٔ شرکت خدماتی دو برابر می‌شود.
- **پیشنهاد اصلاح:** `@Html.FormToken()` روی فرم‌های ثبت + `_formTokens.Stamp(...)` و
  `catch (DbUpdateException) when (_formTokens.IsDuplicate(ex))` در همان الگوی فروش، برای:
  Expense، Loading، LoadingReceipt، Dispatch، LossEvent، SarrafSettlement، TruckSettlement،
  ContractBalanceTransfer، SupplierBalanceTransfer، PartnerSettlement.
- **تست رگرسیون لازم:** بله — همان Probe01 برای هر فرم، با انتظارِ «یکی ثبت، دومی رد».

---

#### PTG-P0-02 — فروشِ با تاریخ گذشته موجودی امروزِ مخزن را منفی می‌کند
- **Severity:** P0 · **Module:** Inventory / Sales · **وضعیت:** **CONFIRMED**
- **سناریو:** مخزن ۱۰۰ MT رسید (۵ جنوری). ۹۰ MT در ۱ جون فروخته می‌شود. بعد کاربر یک فاکتور
  فراموش‌شده با تاریخ ۲۰ جنوری به مقدار ۸۰ MT ثبت می‌کند.
- **مراحل بازتولید:** `Probe03_Backdated_Sale_Drives_Current_Tank_Stock_Negative`
- **انتظار:** فروش دوم رد شود، یا حداقل هشدار بدهد.
- **واقعیت:** هر دو فروش `RedirectToActionResult` (موفق). `received=100 sold=170 closingStock=-70.0000`
- **علت ریشه‌ای:** دو چیز با هم:
  1. `EnsureSufficientTerminalStockAsync` موجودی را با `asOfUtc: saleDate` می‌خواند — یعنی
     «در ۲۰ جنوری ۱۰۰ تن بود» ⇒ قبول.
  2. نگهبانی که دقیقاً برای همین ساخته شده بود خاموش است:
     `private static readonly bool FutureNegativeStockGuardTemporarilyDisabled = true;`
     و `EnsureMovementDoesNotCauseFutureNegativeStockAsync` بلافاصله `return` می‌کند.
- **فایل/متد:** `src/PTGOilSystem.Web/Services/StockService.cs:10` و `:500`
  · `src/PTGOilSystem.Web/Controllers/SalesController.cs:433` `EnsureSufficientTerminalStockAsync`
- **اثر روی دیتابیس:** جمع علامت‌دارِ `InventoryMovements` برای آن (Product, Terminal, Tank) منفی می‌شود.
- **اثر مالی:** COGS و سود همان محموله غلط می‌شود؛ گزارش «موجودی منفی» پر می‌شود؛ فروش بعدی از
  همان مخزن روی موجودی موهوم انجام می‌شود.
- **پیشنهاد اصلاح:** نگهبان را روشن کنید و در عوض «ثبت عقب‌تاریخ» را با تأیید صریح و نقش مجاز
  کنترل کنید (نه با خاموش‌کردن کامل نگهبان). حداقل: اگر ثبت باعث منفی‌شدنِ هر نقطه از خط زمانی شود،
  هشدار بلوکه‌کننده با امکان Override توسط مدیر.
- **تست رگرسیون لازم:** بله — Probe03 با انتظارِ معکوس (`closing >= 0` یا خطای بلوکه).

---

#### PTG-P0-03 — تغییر درصد سهم، سود گذشتهٔ شرکا را بازنویسی می‌کند
- **Severity:** P0 · **Module:** Partnership · **وضعیت:** **CONFIRMED**
- **سناریو:** قرارداد شراکتی ۵۰/۵۰. Partner A خرید (۴۰۰k) را پرداخت کرد، Partner B گمرک (۶۰k) را.
  فروش ۶۰۰k ثبت شد. بعد از تسویه، مدیر درصد را به ۸۰/۲۰ اصلاح می‌کند.
- **مراحل بازتولید:** `Probe04_Changing_SharePercent_Retroactively_Rewrites_Partner_Profit_History`
- **انتظار:** سهم سودِ دوره‌های گذشته دست‌نخورده بماند؛ درصد جدید فقط از این به بعد اثر کند.
- **واقعیت:**
  `before 50/50 -> A=270,000.00 B=270,000.00 bookProfit=540,000.00`
  `after  80/20 -> A=432,000.00 B=108,000.00`
  یعنی ۱۶۲٬۰۰۰ USD بدون هیچ سندی از B به A منتقل شد.
- **علت ریشه‌ای:** `ProfitShareUsd: Round(bookProfitUsd * x.SharePercent / 100m)` — درصد از سطر
  زندهٔ `ContractPartner` خوانده می‌شود. هیچ ستون Snapshot از درصد روی
  `PaymentTransactions`/`SalesTransactions`/`ExpenseTransactions` وجود ندارد (تأیید شد: ۰ ستون).
  ویرایش قرارداد هم `RemoveRange(existing.ContractPartners)` می‌کند و سطرها را از نو می‌سازد،
  پس تاریخچهٔ خودِ درصد هم نمی‌ماند (فقط متن Audit).
- **فایل/متد:** `src/PTGOilSystem.Web/Services/PartyStatements/PartnershipStatementService.cs:925`
  · `src/PTGOilSystem.Web/Controllers/ContractsController.cs:607`
- **اثر مالی:** صورت‌حساب شراکت، پروفایل شریک و P&L قرارداد همگی برای دوره‌های بسته تغییر می‌کنند.
- **پیشنهاد اصلاح:** یا `ContractPartner` را تاریخ‌دار کنید (`EffectiveFrom/EffectiveTo` مثل
  `AssetOwnershipShare`) و مفاد را دوره‌ای محاسبه کنید، یا درصد را در لحظهٔ ثبتِ هر رویداد Snapshot کنید.
- **تست رگرسیون لازم:** بله.

---

#### PTG-P0-04 — دیتابیس سهم‌های ناسازگار را می‌پذیرد و سود بیش از ۱۰۰٪ توزیع می‌شود
- **Severity:** P0 · **Module:** Partnership · **وضعیت:** **CONFIRMED**
- **مراحل بازتولید:** `Probe09_Database_Accepts_Partner_Shares_That_Do_Not_Sum_To_100`
- **واقعیت:** `share total=160% bookProfit=540,000.00 distributed=864,000.00 overDistribution=324,000.00`
- **علت ریشه‌ای:** قاعدهٔ «جمع = ۱۰۰» فقط در `ContractsController.ValidatePartnerShares` است
  (`ContractsController.cs:1325`). نه CHECK constraint، نه Trigger، نه اعتبارسنجی در سرویس.
  هر مسیر دیگری (ایمپورت، `tools/partner-settlement-import`، اسکریپت، اصلاح دستی) می‌تواند آن را بشکند.
- **پیشنهاد اصلاح:** یک اعتبارسنجی در لایهٔ داده (Trigger یا بررسی در `SaveChanges`) + گزارش
  «قراردادهای با سهم ناسازگار» در Reconciliation.
- **تست رگرسیون لازم:** بله.

---

### P1 — باگ بزرگ کسب‌وکار

#### PTG-P1-01 — دفتر کل عملیاتی هیچ قفل دوره‌ای ندارد
- **وضعیت:** CONFIRMED (پیکربندی) · **Module:** Accounting / Ledger
- `PeriodGuard` فقط داخل `AccountingPostingService` استفاده می‌شود، و آن ماژول با
  `appsettings.json → "Accounting": { "Enabled": false }` و **همهٔ ۱۹ Pilot روی false** خاموش است.
- یعنی دفتر کلی که ماندهٔ واقعی طرف‌حساب‌ها را می‌سازد (`LedgerEntries`) هیچ مفهومی از
  «ماه بسته» یا «سال بسته» ندارد. کاربر می‌تواند در دسامبر، سند ژانویه ثبت یا ویرایش کند.
- **اثر:** گزارش‌های ماه‌های قبلی که به مالک/شریک داده شده، ماه بعد عدد دیگری نشان می‌دهند.
- **اصلاح پیشنهادی:** یک قفل دورهٔ سبک روی همین مسیر قدیمی (تاریخ ≥ آخرین دورهٔ بسته) مستقل از
  فعال‌شدن ماژول حسابداری جدید.

#### PTG-P1-02 — ردیف دفتر کل هیچ FK به سند مبدأ ندارد
- **وضعیت:** CONFIRMED · `Probe06_Deleting_A_Posted_Expense_Leaves_An_Orphan_Ledger_Row`
- `DELETE FROM "ExpenseTransactions" WHERE "Id"=…` ⇒ `orphan ledger rows after raw delete: 1`.
- بررسی `information_schema`: هیچ FOREIGN KEY روی `LedgerEntries.SourceId` نیست.
- در UI امروز حذفِ مستقیم بسته است (`HasContractDependenciesAsync` کامل است)، ولی هر اسکریپت،
  ابزار `tools/db-cleaner`، یا Restore ناقص می‌تواند لجر یتیم بسازد و هیچ‌کس متوجه نشود.
- **اصلاح پیشنهادی:** یک بررسی Reconciliation دوره‌ای (orphan ledger) + در بلندمدت ستون‌های
  FK اختیاری per SourceType.

#### PTG-P1-03 — نوشتن در دفتر کل در ~۱۸ نقطه پخش است
- **وضعیت:** CONFIRMED (ساختاری)
- هیچ Posting Service واحدی نیست؛ `Side`، `SourceType`، گِردکردن و پرکردن `AppliedCurrencyPerUsdRate`
  در هر نقطه دستی تکرار شده است.
- **اثر:** هر مسیر جدید (یا هر اصلاح) می‌تواند قاعده را کمی متفاوت پیاده کند و اختلاف بسازد.
- **اصلاح پیشنهادی:** `ILedgerPostingService` با یک ورودی typed؛ مهاجرت تدریجی، بدون تغییر عدد.

#### PTG-P1-04 — ارقام فارسی/عربی کلید ضدتکرارِ بارگیری را دور می‌زنند
- **وضعیت:** CONFIRMED · `Probe08_Persian_Digits_Defeat_The_Loading_Duplicate_Key`
- `english=7|RWB-12345|WGN-98765` · `persian=7|RWB-۱۲۳۴۵|WGN-۹۸۷۶۵` · `arabic=7|RWB-١٢٣٤٥|WGN-٩٨٧٦٥`
- سه کلید متفاوت ⇒ Unique Index روی `LoadingRegister.ImportUniqueKey` بی‌اثر می‌شود و همان واگن
  دوباره ثبت می‌شود.
- `LoadingImportKey.Normalize` فقط فاصله و حروف بزرگ/کوچک را یکسان می‌کند؛ ارقام و
  `ی/ي`، `ک/ك` را نه.
- **فایل:** `src/PTGOilSystem.Web/Helpers/LoadingImportKey.cs`
- **اصلاح پیشنهادی:** یک `TextNormalization` مشترک (ارقام فارسی/عربی → لاتین، یکسان‌سازی ی/ک،
  حذف ZWNJ) و استفادهٔ آن در کلید ایمپورت، جستجو و شماره‌های سند.

#### PTG-P1-05 — نبودِ Concurrency Token روی اسناد مالی — POTENTIAL
- تنها `LoadingReceipt` و موجودیت‌های ماژول حسابداریِ خاموش `RowVersion` دارند.
- `SalesTransaction`، `PaymentTransaction`، `ExpenseTransaction`، `TruckDispatch`، `Contract`،
  `ContractPartner` ندارند ⇒ دو کاربر که هم‌زمان یک پرداخت را ویرایش کنند، آخری بی‌صدا برنده است
  (Lost Update).
- **وضعیت:** POTENTIAL (بازتولید نشد؛ نیاز به دو نشستِ HTTP واقعی دارد).
- **اصلاح پیشنهادی:** `RowVersion (xmin)` روی این موجودیت‌ها + پیام «این رکورد را کاربر دیگری تغییر داد».

---

### P2 — باگ عملکردی

#### PTG-P2-01 — حذف شریکی که فقط «تأمین مالی» کرده، خطای ۵۰۰ می‌دهد — POTENTIAL
- `MasterDataDeleteSafetyService.EvaluatePartnerAsync` فقط `ContractPartners` را چک می‌کند.
  `PaymentTransaction.PaidByPartnerId`، `PartnerSettlement.From/ToPartnerId`،
  `Contract.SaleProceedsHolderPartnerId` و `AssetOwnershipShare.PartnerId` بررسی نمی‌شوند.
- خوشبختانه هر چهار FK `DeleteBehavior.Restrict` هستند ⇒ **داده خراب نمی‌شود**، ولی
  `PartnersController.Delete` نتیجهٔ `CanDelete=true` می‌گیرد، `Remove` می‌کند و
  `DbUpdateException` مدیریت‌نشده به کاربر خطای سرور نشان می‌دهد.
- **فایل:** `src/PTGOilSystem.Web/Services/DeleteSafety/MasterDataDeleteSafetyService.cs:184`
  · `src/PTGOilSystem.Web/Controllers/PartnersController.cs:230`

#### PTG-P2-02 — ایمپورت اکسل «همه یا هیچ» است
- یک ردیف تکراری (`ValidateNoDuplicateLoadingsAsync`) کل فایل ۱۰۰۰ ردیفی را رد می‌کند و کاربر
  باید فایل را دستی اصلاح کند. اتمی‌بودن درست است، ولی راه «ردیف‌های سالم را ثبت کن، تکراری‌ها را
  گزارش کن» وجود ندارد.
- **فایل:** `src/PTGOilSystem.Web/Controllers/LoadingController.cs:4881`

#### PTG-P2-03 — فروش عملاً قابل اصلاح نیست
- `SalesController.Edit` هر تغییری جز «یادداشت» را رد می‌کند
  («در نسخه فعلی فقط ویرایش یادداشت فروش مجاز است»).
- از نظر یکپارچگی داده درست است، ولی در عمل هر اشتباه تایپی در مقدار/قیمت نیازمند لغو و صدور
  دوبارهٔ فاکتور است — و مسیر «لغو» خودش سند و شمارهٔ فاکتور جدید می‌خواهد.

---

### P3 — UX / Validation

#### PTG-P3-01 — محافظ ثبت دوباره فقط سمت مرورگر است (برای اکثر فرم‌ها)
- `wwwroot/js/core.js` دکمهٔ Submit را قفل می‌کند، ولی این محافظ در Refresh، تب دوم، یا
  بازگشت با دکمهٔ Back از بین می‌رود. برای فرم‌های بدون توکن سرور (PTG-P0-01) تنها لایهٔ دفاعی همین است.

#### PTG-P3-02 — تاریخ گذشته/آینده بدون هیچ هشداری پذیرفته می‌شود
- نه محدودیت، نه هشدار، نه نشانه‌گذاری «Backdated» روی سند. ترکیب این با PTG-P0-02 و PTG-P1-01
  یعنی یک اشتباه تاریخ می‌تواند ماه‌ها بعد کشف شود.

---

### P4 — کارایی / نگهداشت

#### PTG-P4-01 — گردش حساب طرف‌حساب کل تاریخچه را بدون صفحه‌بندی می‌خواند
- **اندازه‌گیری‌شده:** ۳٬۹۷۸ ms برای مشتری با ۱۵۰٬۰۰۰ سطر لجر (تأمین‌کننده: ۱٬۹۴۹ ms).
- `PartyStatementReadService.BuildLedgerRowsAsync` همهٔ سطرها را `ToListAsync` می‌کند تا
  «مانده اول دوره» را بسازد؛ فیلتر تاریخ اجباری نیست.
- **اصلاح پیشنهادی:** مانده اول دوره را با یک `SUM` جدا بگیرید و فقط سطرهای داخل بازه را بخوانید؛
  بازهٔ تاریخ پیش‌فرض (مثلاً سال مالی جاری) اجباری شود.

#### PTG-P4-02 — صفحه‌بندی دفتر کل با OFFSET
- ۳۵۷ ms در offset ۲۵۰٬۰۰۰. الان مشکلی نیست، ولی خطی رشد می‌کند. Keyset pagination گزینهٔ آینده است.

---

## ۶. چیزهایی که سالم بودند (تأییدشده، برای اینکه دوباره خراب نشوند)

| موضوع | مدرک |
|---|---|
| قفل هم‌زمانیِ مخزن در فروش | `Probe05`: دو فروش ۷۰ MT هم‌زمان از موجودی ۱۰۰ ⇒ فقط یکی ثبت شد، `closingStock=30` |
| Idempotency فروش | `Probe02`: دو POST با همان توکن ⇒ فقط ۱ فروش |
| دقت ارز | `Probe07`: خطای رفت‌وبرگشت AFN روی ۲۰۰ ردیف = **۰.۰۰۰۰ AFN** (به لطف `numeric(24,12)` برای نرخ و نگه‌داشتنِ جداگانهٔ `AppliedCurrencyPerUsdRate`) |
| ستون‌های پولی | همه `numeric(18,4)`، نرخ‌ها `numeric(18,6)`/`numeric(24,12)` — هیچ `float`ی در مسیر مالی نیست |
| ویرایش مصرف/پرداخت | سطر لجر در جای خود به‌روز می‌شود (نه تکراری)، داخل Transaction، با Audit |
| ویرایش قرارداد | بارگیری‌های قیمت‌گذاری‌شده دوباره قیمت‌گذاری نمی‌شوند (`repriceFinalized: false`) |
| حذف قرارداد | `HasContractDependenciesAsync` ۱۹ رابطه را چک می‌کند، از جمله `LedgerEntries` |
| کرایه دیسپچ | `DispatchFreightExpenseSync` idempotent است و مصرف‌های تکراری را خودش Cancel می‌کند |
| نرمال‌سازی تاریخ | `NormalizeDateTimePropertiesToUtc` در `SaveChanges`؛ «امروزِ کاری» از ساعت کابل (`AfghanistanBusinessClock`) |
| ایندکس‌ها | ۵۹۴ ایندکس؛ هیچ‌کدام از ستون‌های داغِ فیلتر/مرتب‌سازی بدون ایندکس نبود |

---

## TOP 10 RISKS BEFORE REAL CUSTOMER DEPLOYMENT

1. **ثبت دوبارهٔ اسناد مالی روی اینترنت ضعیف** — مصرف، بارگیری، رسید، دیسپچ، ضایعات، تسویهٔ صراف
   هیچ‌کدام محافظ سرور ندارند. (PTG-P0-01)
2. **منفی‌شدن موجودی با ثبت عقب‌تاریخ** — نگهبانِ مخصوصِ همین سناریو در کد خاموش است. (PTG-P0-02)
3. **بازنویسی سود شرکا با تغییر درصد سهم** — تسویه‌های امضاشدهٔ گذشته عوض می‌شوند. (PTG-P0-03)
4. **نبودِ قفل دوره در دفتر کل عملیاتی** — گزارش ماه بسته، ماه بعد عدد دیگری می‌دهد. (PTG-P1-01)
5. **پذیرش سهم شراکت ≠ ۱۰۰٪ در سطح داده** — توزیع سود بیش از سود واقعی. (PTG-P0-04)
6. **ارقام فارسی، ضدتکرارِ ایمپورت بارگیری را بی‌اثر می‌کند** — بار دوبار وارد موجودی. (PTG-P1-04)
7. **Lost Update روی اسناد مالی** — بدون Concurrency Token روی فروش/پرداخت/مصرف/دیسپچ. (PTG-P1-05)
8. **پراکندگی نوشتن در دفتر کل در ۱۸ نقطه** — هر تغییر آینده ریسک واگرایی دارد. (PTG-P1-03)
9. **گردش حساب طرف‌حساب بدون صفحه‌بندی** — تنها مسیری که در حجم چندساله به مرز ۴ ثانیه رسید. (PTG-P4-01)
10. **نبودِ FK بین لجر و سند مبدأ** — هر عملیات خارج از UI می‌تواند بی‌صدا لجر یتیم بسازد. (PTG-P1-02)

## TOP 10 FIXES WITH HIGHEST BUSINESS IMPACT

| # | اصلاح | اثر | تخمین |
|---|---|---|---|
| ۱ | `FormToken` روی همهٔ فرم‌های ثبتِ مالی/عملیاتی (الگوی موجودِ فروش) | حذف کامل خطر سند تکراری | کوچک، تکراری |
| ۲ | روشن‌کردن `EnsureMovementDoesNotCauseFutureNegativeStockAsync` + مسیر صریح «ثبت عقب‌تاریخ با تأیید» | جلوگیری از موجودی منفی و COGS غلط | متوسط |
| ۳ | تاریخ‌دار کردن `ContractPartner` (`EffectiveFrom/To`) یا Snapshot درصد روی هر رویداد | ثبات تسویهٔ شرکا | متوسط–بزرگ |
| ۴ | قفل دوره روی دفتر کل عملیاتی (مستقل از ماژول حسابداری جدید) | گزارش ماه بسته قابل اتکا | متوسط |
| ۵ | اعتبارسنجی «جمع سهم = ۱۰۰» در لایهٔ داده + گزارش قراردادهای ناسازگار | جلوگیری از توزیع سود اضافی | کوچک |
| ۶ | `TextNormalization` مشترک (ارقام فارسی/عربی، ی/ک، ZWNJ) در کلید ایمپورت و جستجو | جلوگیری از بار تکراری در موجودی | کوچک |
| ۷ | `RowVersion` روی `SalesTransaction`/`PaymentTransaction`/`ExpenseTransaction`/`TruckDispatch`/`Contract` | حذف Lost Update | کوچک + Migration |
| ۸ | صفحه‌بندی + بازهٔ تاریخ اجباری در گردش حساب طرف‌حساب (مانده اول دوره با `SUM` جدا) | ۴ ثانیه → زیر ۲۰۰ میلی‌ثانیه | متوسط |
| ۹ | `ILedgerPostingService` واحد و مهاجرت تدریجی ۱۸ نقطه به آن | جلوگیری از واگرایی آینده | بزرگ |
| ۱۰ | کامل‌کردن `EvaluatePartnerAsync` (funding، settlement، proceeds holder، asset share) + مدیریت `DbUpdateException` | حذف خطای ۵۰۰ و پیام درست | کوچک |

---

## ۷. پاسخ به سؤال اصلی

> «اگر یک شرکت واقعی نفت و گاز در افغانستان یک سال هر روز با PTG Oil System کار کند،
> کدام قسمت اول خراب می‌شود؟»

**۱) موجودی مخزن** — از دو راه: ثبت عقب‌تاریخِ فروش (تأییدشده) و ورودِ دوبارهٔ بارگیری با
شماره‌های فارسی‌نویس‌شده. هر دو بی‌صدا هستند و ماه‌ها بعد در «تحلیل موجودی منفی» ظاهر می‌شوند.

**۲) هزینه‌ها و ماندهٔ شرکت‌های خدماتی** — به‌خاطر ثبت دوبارهٔ مصرف روی اینترنت ضعیف.

**۳) حساب شراکت** — به محض اولین اصلاح درصد سهم، همهٔ تسویه‌های قبلی عدد دیگری می‌گیرند.

ریاضیِ خودِ سیستم سالم است؛ ۱۲ ماه شبیه‌سازی هیچ اختلافی بین موجودی، لجر و اسناد نشان نداد.
شکست از **مسیر ورود داده** می‌آید: تکرار، تاریخ گذشته، و تغییر پارامترِ زنده‌ای که تاریخچه به آن وابسته است.

---

*منتظر دستور بعدی برای شروع اصلاحات.*
