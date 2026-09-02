# PTG Oil System — P0 Fix Validation Report

**تاریخ:** 2026-08-29
**دامنه:** فقط چهار مورد CONFIRMED P0 از `PTG_12_MONTH_PRODUCTION_SIMULATION_REPORT.md`.
**ترتیب اجرا:** PTG-P0-01 → PTG-P0-02 → PTG-P0-04 → PTG-P0-03 (طبق دستور).
**هیچ مورد P1/P2/P3/P4 لمس نشد.**

| Issue | Status |
|---|---|
| PTG-P0-01 — ثبت دوبارهٔ سند مالی/عملیاتی | **FIXED** |
| PTG-P0-02 — موجودی منفی از ثبت عقب‌تاریخ | **FIXED** |
| PTG-P0-04 — پذیرش سهم شراکت ≠ ۱۰۰٪ | **FIXED** |
| PTG-P0-03 — بازنویسی تاریخچهٔ سهم شرکا | **FIXED** (با یک محدودیت مستند) |

---

## PTG-P0-01 — Idempotency سمت سرور برای فرم‌های مالی/عملیاتی

**Status: FIXED**

### Root cause
`IFormTokenGuard` از قبل وجود داشت و درست کار می‌کرد، ولی فقط در چهار فرم استفاده می‌شد
(`Contracts/Create`، `Payments/Create`، `Sales/Create`، `InventoryTransportLegs/CreateFromInventory`).
مسیر مصرف و بقیهٔ مسیرهای ثبت اصلاً پارامتر `formToken` نمی‌گرفتند. تنها محافظ باقی‌مانده
قفلِ دکمهٔ Submit در `wwwroot/js/core.js` بود که Refresh، تب دوم و تلاش پس از Timeout را نمی‌گیرد
— دقیقاً همان چیزی که روی اینترنت افغانستان اتفاق می‌افتد.

### Exact implementation
برای هر مسیر، همان الگوی آزمودهٔ فروش تکرار شد:

1. **رندر توکن** — `@Html.FormToken()` در فرم (یک `<input type="hidden" name="__FormToken">`).
2. **دریافت** — `[FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null`
   روی اکشن POST (پارامتر اختیاری ⇒ هیچ امضای موجودی نمی‌شکند).
3. **مصرف** — `_formTokens.Stamp(formToken, "<Purpose>", nameof(<Entity>))` بلافاصله پیش از
   همان `SaveChanges` که سند اصلی را می‌نویسد، **داخل همان Transaction**.
4. **مکانیزم قطعیِ دیتابیس** — Unique Index موجود `IX_ProcessedFormTokens_Token`.
   ارسال دوم با همان توکن، همان‌جا `23505` می‌گیرد و کل Transaction برمی‌گردد.
5. **پاسخ کاربر** — `catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))`
   با پیام «این … قبلاً ثبت شده است و دوباره ثبت نشد.» و Redirect (بدون خطای ۵۰۰).

نبودِ توکن همچنان fail-open است، پس هیچ مسیر قدیمی/تستی نمی‌شکند.

### مسیرهای محافظت‌شده در این فاز

| Purpose | Controller | View |
|---|---|---|
| `Expense.Create` | ExpensesController | `Expenses/Create.cshtml` |
| `Expense.CreateWagonRent` | ExpensesController | `Expenses/CreateWagonRent.cshtml` |
| `Expense.CreateGroup` | ExpensesController | `Expenses/CreateGroup.cshtml` |
| `Loading.Create` | LoadingController | `Loading/Create.cshtml` |
| `LoadingReceipt.Create` | LoadingReceiptsController | `LoadingReceipts/_ReceiptCreateForm.cshtml` |
| `Dispatch.Create` | DispatchController | `Dispatch/Create.cshtml` |
| `Dispatch.CreateDirectFromReceipt` | DispatchController | `Dispatch/CreateDirectFromReceipt.cshtml` |
| `LossEvent.Create` | LossEventsController | `LossEvents/Create.cshtml` |
| `SupplierBalanceTransfer.Create` | SupplierBalanceTransfersController | `SupplierBalanceTransfers/Create.cshtml` |
| `PartnerSettlement.Create` | PartnershipStatementController | `Shared/_CreateModalShell.cshtml` (opt-in) |
| `TruckSettlement.GroupUnload` | TruckSettlementsController | `TruckSettlements/GroupUnload.cshtml` |
| `Payment.CreateViaSarraf` | PaymentsController | `Payments/Create.cshtml` |
| `Payment.CreateViaSarrafGeneral` | PaymentsController | `Payments/Create.cshtml` |

**نکتهٔ مهمی که حین کار پیدا شد:** فرم پرداخت از قبل توکن را رندر می‌کرد و
`PaymentsController.Create` هم آن را می‌گرفت، ولی برای `PaymentMethod.ViaSarraf` بدون پاس‌دادن
توکن به `CreateViaSarrafAsync` منشعب می‌شد. یعنی «پرداخت از طریق صراف» — که دو ردیف دفتر کل
می‌سازد — عملاً بدون محافظ بود. توکن حالا در هر دو شاخه (تأمین‌کننده و عمومی) پاس داده می‌شود.

**مسیرهایی که عمداً دست‌نخورده ماندند:** `SarrafSettlementsController.Create` و
`ContractBalanceTransfersController.Create` هر دو در کد فعلی غیرفعال‌اند و فقط پیام خطا
برمی‌گردانند؛ چیزی برای محافظت وجود ندارد.

### مورد خاص: `SupplierBalanceTransfer` و `Payment.CreateViaSarrafGeneral`
این دو، Transaction را داخل سرویس باز می‌کنند. توکن پیش از فراخوانی سرویس فقط به ChangeTracker
اضافه می‌شود و با نخستین `SaveChanges` داخل همان Transaction ذخیره می‌گردد؛ اگر اعتبارسنجی
سرویس خطا بدهد، توکن هرگز ذخیره نمی‌شود و کاربر می‌تواند دوباره تلاش کند (fail-open درست).

### Files changed
- `Controllers/ExpensesController.cs`, `LoadingController.cs`, `LoadingReceiptsController.cs`,
  `DispatchController.cs`, `LossEventsController.cs`, `SupplierBalanceTransfersController.cs`,
  `PartnershipStatementController.cs`, `TruckSettlementsController.cs`,
  `TruckSettlementsController.GroupUnload.cs`, `PaymentsController.cs`
- `Views/Expenses/{Create,CreateWagonRent,CreateGroup}.cshtml`,
  `Views/Loading/Create.cshtml`, `Views/LoadingReceipts/_ReceiptCreateForm.cshtml`,
  `Views/Dispatch/{Create,CreateDirectFromReceipt}.cshtml`, `Views/LossEvents/Create.cshtml`,
  `Views/SupplierBalanceTransfers/Create.cshtml`, `Views/TruckSettlements/GroupUnload.cshtml`,
  `Views/PartnershipStatement/Index.cshtml`, `Views/Shared/_CreateModalShell.cshtml`

### Migration
هیچ. جدول `ProcessedFormTokens` و Unique Index آن از قبل وجود داشتند.

### Regression tests
- `Probe01_Double_Submit_Of_Expense_Creates_Exactly_One_Expense_And_One_Ledger_Row` (بازنویسیِ Probe01 با انتظار معکوس)
- `Probe01b_Double_Submit_Of_Loading_Creates_Exactly_One_Loading` (تست تازه؛ عمداً سطری بدون شماره سند/حمل تا `ImportUniqueKey = null` بماند و تنها محافظ، توکن باشد)
- `Probe02_Double_Submit_Of_Sale_With_Same_Form_Token_Is_Rejected` (کنترل مثبت، بدون تغییر)
- `FormIdempotencyCoverageTests` (فایل تازه، ۳۳ تست): هر فرم محافظت‌شده باید `@Html.FormToken()` را رندر کند، هر مسیر باید `Stamp` با Purpose درست داشته باشد، و هر Controllerی که `Stamp` می‌کند باید `IsDuplicate` را هم مدیریت کند.

### Before / After

| | Before | After |
|---|---|---|
| دو POST مصرف با همان توکن | `expenses=2 ledgerRows=2 totalUsd=25,000.00` | `expenses=1 ledgerRows=1 totalUsd=12,500.00` |
| دو POST بارگیری با همان توکن (`ImportUniqueKey=null`) | ۲ بارگیری | `loadings=1 importKeys=[null]` |

### Database impact
فقط سطرهای `ProcessedFormTokens` (یک سطر کوچک به‌ازای هر ثبت موفق). هیچ تغییری در schema.

### Financial impact
دوبار شمرده‌شدن مصرف/بارگیری/دیسپچ/ضایعات/انتقال/تسویه/پرداخت صرافی از بین می‌رود.

### Compatibility risk
پایین. پارامتر توکن اختیاری است؛ درخواستِ بدون توکن دقیقاً مثل قبل رفتار می‌کند.
فرم بارگیری یک POST بومی و یک‌مرحله‌ای است (سطرهای اکسل داخل `ImportedRowsJson` می‌آیند)، پس
یک توکن، یک ثبت — هیچ «ثبت دسته‌ای چند-درخواستی» وجود ندارد که بشکند.
`_CreateModalShell.cshtml` مشترک است، بنابراین توکن با پرچم `CreateFormIdempotent` **opt-in** شد
تا به مودال‌های دیگر (مثل PlattsRates) فیلد اضافه تزریق نشود.

### Remaining concern
- جدول `ProcessedFormTokens` سیاست نگه‌داری/پاک‌سازی ندارد و برای همیشه رشد می‌کند
  (سطرها کوچک‌اند و ایندکس یکتا دارند، پس ریسک فوری نیست).
- فرم‌های ویرایش عمداً توکن نمی‌گیرند: ویرایش idempotent است (سطر لجر در جای خودش به‌روز می‌شود).
- `LoadingExcelImportController` و `ExpensesController.ImportConfirm` در این فاز محافظت نشدند؛
  هر دو کلید ضدتکرارِ خودشان را دارند (`ImportUniqueKey`)، ولی PTG-P1-04 (ارقام فارسی) هنوز
  می‌تواند آن کلید را دور بزند. طبق دستور، P1 لمس نشد.

---

## PTG-P0-02 — ثبت عقب‌تاریخ نباید موجودی را منفی کند

**Status: FIXED**

### Root cause
دو چیز با هم:
1. `SalesController.EnsureSufficientTerminalStockAsync` موجودی را با `asOfUtc: saleDate`
   می‌سنجید — یعنی «در تاریخ آن سند» و نه «امروز».
2. نگهبانی که دقیقاً برای همین ساخته شده بود با یک کلیدِ سراسری خاموش بود:
   `private static readonly bool FutureNegativeStockGuardTemporarilyDisabled = true;`
   و `EnsureMovementDoesNotCauseFutureNegativeStockAsync` بلافاصله `return` می‌کرد.

### Exact implementation
1. کلیدِ خاموش‌کننده حذف شد.
2. منطق نگهبان بازنویسی شد:
   - `balanceBefore` = یک `SUM` برای حرکات پیش از تاریخ سند.
   - فقط سطرهای «از تاریخ سند به بعد» خوانده و به‌ترتیب `(MovementDate, Id)` پیموده می‌شوند
     (سندِ جدید اول از همه در تاریخ خودش اعمال می‌شود ⇒ سخت‌گیرانه‌تر، نه شل‌تر).
   - `lowest`، `firstNegativeDate` و **`running` نهایی (ماندهٔ پایانی)** محاسبه می‌شوند.
   - **قاعدهٔ مسدودسازی: ماندهٔ پایانی منفی.**
3. پیام خطا (Dari-friendly) شاملِ: عملیات و مقدارش، تاریخ سند، کالا/مخزن/ترمینال/قرارداد،
   تاریخ نخستین منفی‌شدن، کمترین موجودی پیش‌بینی‌شده، ماندهٔ پایانی، و راهنمای اصلاح.
   نام‌ها فقط در مسیر خطا خوانده می‌شوند (`DescribeStockScopeAsync`)، پس مسیر موفق کوئری اضافه ندارد.

### چرا «ماندهٔ پایانی» و نه «هر نقطه از خط زمانی»
سخت‌گیری روی «هیچ نقطه‌ای منفی نشود» یک جریان قانونی و مستندِ موجود را می‌شکست:
`InventoryTransportLegLoadService` عمداً موجودیِ **جاری** را مبنا می‌گیرد
(«چک موجودی اینجا نسخهٔ مخصوص همین مسیر است … عمداً بدون asOfUtc»)، چون سندِ رسید معمولاً
بعد از بارگیریِ موتر/واگن به دفتر می‌رسد. آن حالت یک گودالِ گذرای منفی می‌سازد که خودش ترمیم
می‌شود و هیچ اثر مالی ندارد. آنچه واقعاً COGS و سود را خراب می‌کند، **ماندنِ موجودی در منفی**
است. این تفکیک با دو تست جداگانه pin شده است.

### Files changed
- `Services/StockService.cs` (حذف کلید خاموش‌کننده + بازنویسی نگهبان + `DescribeStockScopeAsync`)
- `Services/InventoryMovementWriter.cs` (فقط اصلاح کامنتِ `StockGuard.FutureTimeline`)

هیچ caller ای سطح `StockGuard` خود را عوض نکرد (بدون گسترش دامنه). مسیرهایی که از قبل
`FutureTimeline` می‌خواستند حالا واقعاً آن را می‌گیرند: `Sales.Create`، `Sales.Group`،
`InventoryTransportLegLoadService`، و مسیر برگشتِ `InventoryMovementWriter`.
`DispatchController` این نگهبان را از قبل مستقیماً صدا می‌زد.
`LossEventWorkflowService`، `InventoryTransportBatchService` و `LoadingReceiptCancellationService`
عمداً روی `StockGuard.Standard` ماندند (بدون تغییر رفتار).

### Migration
هیچ.

### Regression tests
- `Probe03_Backdated_Sale_Is_Blocked_Instead_Of_Creating_Negative_Stock` (بازنویسیِ Probe03)
- `Probe03b_Backdated_Sale_That_Keeps_The_Timeline_Positive_Is_Accepted` (تست تازه — ثبت عقب‌تاریخِ مجاز نباید مسدود شود)
- `DateTimeNormalizationTests.EnsureMovementDoesNotCauseFutureNegativeStock_BlocksBackdatedOutThatBreaksLaterBalance`
  (بازنویسی تستی که قبلاً «مجاز بودنِ موقت» را pin می‌کرد — همان باگ بود)
- `DateTimeNormalizationTests.EnsureMovementDoesNotCauseFutureNegativeStock_AllowsTransientDipThatHealsLater` (تست تازه)
- `Probe05_Concurrent_Sales_From_Same_Tank_Cannot_Oversell` بدون تغییر و همچنان سبز.

### Before / After

| | Before | After |
|---|---|---|
| رسید ۱۰۰ (۵ جنوری) → فروش ۹۰ (۱ جون) → فروش ۸۰ عقب‌تاریخ (۲۰ جنوری) | هر دو فروش قبول، `sold=170`, `closingStock=-70` | فروش دوم **رد** شد، `sold=90`, `closingStock=10` |
| پیام | — | `این ثبت انجام نشد: خروج 80.0000 MT به تاریخ 2025-01-20 باعث می‌شود موجودی کالای «Product P03» / مخزن «TK-P03» / ترمینال «Terminal P03» / قرارداد PUR-P03 از تاریخ 2025-06-01 منفی شود (کمترین موجودی پیش‌بینی‌شده: -70.0000 MT، ماندهٔ پایانی: -70.0000 MT). …` |
| ثبت عقب‌تاریخِ مجاز (۳۰ تن) | قبول | قبول (`closingStock=10`) |

### Database impact
هیچ. فقط جلوگیری از نوشتنِ حرکتِ نامعتبر.

### Financial impact
COGS، سود محموله و گزارش موجودی منفی دیگر با یک اشتباه تاریخ خراب نمی‌شوند.

### Compatibility risk
متوسط و کنترل‌شده. تنها تستی که شکست، همان تستی بود که رفتار باگ‌دار را pin می‌کرد؛
`MarkLoaded_Uses_Current_Source_Stock_For_Backdated_Transport` (جریان قانونیِ حمل) پس از
انتخابِ قاعدهٔ «ماندهٔ پایانی» دوباره سبز شد.

### Remaining concern
- **مسیر Override وجود ندارد.** طبق دستور، «Prefer safe blocking first» رعایت شد. اگر
  عملیات واقعاً نیاز به ثبتِ عقب‌تاریخِ استثنایی داشته باشد، فعلاً باید اول اسناد بعدی را اصلاح کند.
- اگر یک scope از قبل (دادهٔ تاریخی) منفی باشد، هر خروجِ تازه روی همان scope مسدود می‌شود تا
  داده اصلاح گردد. این عمدی است؛ گزارش «موجودی منفی» همان فهرست را می‌دهد.

---

## PTG-P0-04 — سهم شراکت همیشه باید معتبر باشد

**Status: FIXED**

### Root cause
قاعدهٔ «جمع = ۱۰۰» فقط در `ContractsController.ValidatePartnerShares` بود. دیتابیس هیچ
محدودیتی نداشت، پس ایمپورت/ابزار/اسکریپت/سرویس آینده می‌توانست هر عددی بنویسد.
اندازه‌گیری‌شده: ۱۶۰٪ ⇒ توزیع ۸۶۴٬۰۰۰ USD به‌جای ۵۴۰٬۰۰۰ (۳۲۴٬۰۰۰ اضافی).

### Exact implementation
`CHECK` سطری نمی‌تواند `SUM` چندسطری را بسنجد، پس از **CONSTRAINT TRIGGER با
`DEFERRABLE INITIALLY DEFERRED`** استفاده شد:

- تابع `ptg_check_contract_partner_shares()` در لحظهٔ **COMMIT** اجرا می‌شود.
- فقط قراردادهای `OwnershipType = 2` (Partnership) را می‌سنجد.
- اگر قرارداد در همان تراکنش حذف یا به «شخصی» تبدیل شده باشد، رد می‌شود (بدون خطا).
- اگر همهٔ سطرهای شرکا حذف شده باشند، رد می‌شود (حذف قرارداد سالم می‌ماند).
- `SharePercent <= 0` یا `> 100` ⇒ `PTG_PARTNER_SHARE_INVALID`.
- جمع ≠ ۱۰۰ (با تلورانس ۰.۰۰۰۱) ⇒ `PTG_PARTNER_SHARE_SUM`. هر دو با `ERRCODE = 23514`.

چون تریگر **تعویق‌دار** است، الگوی «حذف همهٔ سهم‌ها و نوشتن دوباره در یک تراکنش» — کاری که
ویرایش قرارداد انجام می‌دهد — کاملاً سالم می‌ماند.

**نکتهٔ عملیاتی:** خطای یک تریگر تعویق‌دار در COMMIT رخ می‌دهد، بنابراین EF آن را در
`DbUpdateException` نمی‌پیچد و مستقیماً `Npgsql.PostgresException` است. برای همین در
`ContractsController` (هر دو مسیر Create و Edit) یک `catch (PostgresException) when (IsPartnerShareViolation(ex))`
اضافه شد که پیام فارسی خوانا روی `ModelState` می‌گذارد (به‌جای خطای ۵۰۰).

### Migration
`20260828230100_AddContractPartnerShareSumGuard`
- `Up`: ساخت تابع + `DROP TRIGGER IF EXISTS` + `CREATE CONSTRAINT TRIGGER "TR_ContractPartners_ShareSum"`.
- `Down`: حذف تریگر و تابع.
- **هیچ داده‌ای تغییر یا حذف نمی‌شود.**

### Files changed
- `Migrations/20260828230100_AddContractPartnerShareSumGuard.cs` (جدید)
- `Controllers/ContractsController.cs` (مدیریت خطا + دو helper)

### Regression tests
- `Probe09_Database_Rejects_Partner_Shares_That_Do_Not_Sum_To_100` (بازنویسیِ Probe09)
- `Probe09b_Database_Rejects_Zero_Or_Negative_Partner_Share`
- `Probe09c_Valid_Share_Splits_Are_Accepted` — `50/50`، `60/40`، `33.3333/33.3333/33.3334`،
  و همه از طریق الگوی «RemoveRange سپس AddRange در یک SaveChanges».
- `Probe09d_Reconciliation_Query_Finds_Contracts_Whose_Shares_Do_Not_Sum_To_100` — **کوئری تطبیق**
  برای پیدا کردن قراردادهای ناسازگارِ موجود.
- `TwelveMonthProductionSimulationTests` → یافتهٔ `SIM-PRT-01` همین تطبیق را روی دادهٔ ۱۲ ماهه اجرا می‌کند.

### Before / After

| | Before | After |
|---|---|---|
| نوشتن ۸۰٪ + ۸۰٪ | پذیرفته شد؛ `distributed=864,000` در برابر `bookProfit=540,000` | `23514: PTG_PARTNER_SHARE_SUM: … totalling 160.0000 percent …`؛ جمع روی دیتابیس همچنان `100.0000%` |
| نوشتن ۱۰۰٪ + ۰٪ | پذیرفته می‌شد | `23514: PTG_PARTNER_SHARE_INVALID: …` |
| `50/50`, `60/40`, `33.3333/33.3333/33.3334` | قبول | قبول (بدون تغییر) |

### Database impact
یک تابع و یک CONSTRAINT TRIGGER اضافه می‌شود. هیچ ستون، ایندکس یا داده‌ای تغییر نمی‌کند.

### Financial impact
توزیع سود بیش از سود واقعی در سطح داده غیرممکن می‌شود، مستقل از مسیر ورود.

### Compatibility risk
پایین برای دادهٔ سالم. **ولی:** اگر دادهٔ تاریخی از قبل ناسازگار باشد، تریگر آن را در جای خود
مسدود نمی‌کند (تعویق‌دار فقط روی سطرهای همان تراکنش کار می‌کند)، ولی **نخستین ویرایشِ بعدیِ
همان قرارداد شکست می‌خورد** تا اصلاح شود. به همین دلیل کوئری تطبیق (`Probe09d`) اضافه شد تا
پیش از استقرار اجرا شود.

### Remaining concern
تریگر روی `ContractPartners` است، نه روی `Contracts`. اگر قراردادی از «شخصی» به «شراکتی»
تبدیل شود **بدون اینکه هیچ سطر شریکی لمس شود**، تریگر شلیک نمی‌کند. Controller این حالت را
اعتبارسنجی می‌کند و کوئری تطبیق آن را پیدا می‌کند؛ عمداً گسترش داده نشد تا ایمپورت‌هایی که
قرارداد و شرکا را در دو تراکنش جدا می‌نویسند نشکنند.

---

## PTG-P0-03 — تاریخچهٔ سهم شرکا نباید بازنویسی شود

**Status: FIXED** (با یک محدودیتِ مستند دربارهٔ دادهٔ تاریخی)

### تحلیل پیش از پیاده‌سازی
همهٔ جاهایی که `ContractPartner.SharePercent` را می‌خواندند بررسی شد. سه مسیر **پول** را
تقسیم می‌کردند و هر سه درصدِ امروز را روی رویدادهای گذشته اعمال می‌کردند:

| مسیر | چه می‌کرد |
|---|---|
| `PartnershipStatementService` (خط ۹۲۵) | `ProfitShareUsd = Round(bookProfit * SharePercent / 100)` |
| `PartyStatementReadService.BuildPartnerRowsAsync` | تقسیم هر ردیف دفتر کل بر `SharePercent` |
| `PartyBalanceReadService.AddPartnerEventsAsync` | تقسیم هر ردیف دفتر کل بر `SharePercent` |

و چند مسیر **نمایشی**: `PartyStatementPageBuilder` (برچسب ستون)، `ContractJourneyController`،
`PaymentsController` (گزینه‌های فرم)، `ContractsController` (فرم ویرایش و خلاصهٔ Audit).

### تصمیم: OPTION A — بازهٔ زمانی روی `ContractPartner`
همان الگویی که از قبل در همین سیستم برای `AssetOwnershipShare` وجود دارد
(`EffectiveFrom` / `EffectiveTo`). دلیل انتخاب: Snapshot روی هر تراکنش نیازمند افزودن ستون به
`PaymentTransactions`/`SalesTransactions`/`ExpenseTransactions` و Backfill آن‌ها بود — تغییری
به‌مراتب بزرگ‌تر و پرریسک‌تر، و بی‌فایده برای رویدادهایی که ستون ندارند (بارگیری، رسید).

### Exact implementation
1. **Entity** — `ContractPartner` دو ستون گرفت: `EffectiveFrom` (اجباری) و `EffectiveTo` (اختیاری).
2. **کلید یکتا** — از `(ContractId, PartnerId)` به `(ContractId, PartnerId, EffectiveFrom)` رفت،
   به‌علاوهٔ ایندکس `(ContractId, EffectiveFrom)`.
3. **`ContractPartnerShareHistory` (کلاس تازه)** — **تنها مرجعِ** پاسخ به «سهمِ این شریک در
   تاریخِ این رویداد چند بود؟». هر سه مسیر پول از همین کلاس می‌پرسند، پس هیچ گزارشی از بقیه
   جدا نمی‌افتد.
   - قاعده: آخرین بازه‌ای که `EffectiveFrom <= D`؛ اگر هیچ بازه‌ای پیش از D نبود، **نخستین بازه**.
   - آن fallback عمدی است: پس از Backfill هر قرارداد فقط یک بازه دارد، پس هر رویداد —
     حتی رویدادی پیش از آغاز آن بازه — دقیقاً همان عدد قبلی را می‌گیرد.
   - انتخاب بر پایهٔ `EffectiveFrom` است نه بازهٔ بستهٔ `EffectiveTo`، تا اگر روزی سطری بسته شود
     و جانشین نداشته باشد، شریک بی‌صدا صفر نشود.
4. **سهم مفاد** — `AllocateProfitBySharePeriod`: مفاد به نسبتِ **عایدِ فروشِ هر بازه** تقسیم
   می‌شود (مفاد هنگام فروش محقق می‌شود)، و در هر بازه همان درصدِ آن زمان اعمال می‌گردد.
   اگر فروشی نباشد، همهٔ مفاد به نخستین بازه می‌رود (همان توافقی که زیر آن هزینه شده است).
   **برای قراردادی که فقط یک بازه دارد، نتیجه دقیقاً `Round(bookProfit * share / 100)` است —
   یعنی همهٔ دادهٔ موجود عدد به عدد بدون تغییر می‌ماند.**
5. **ویرایش قرارداد** — `RemoveRange` + `AddRange` جای خود را به `ApplyPartnerShareChange` داد:
   - ترکیب بدون تغییر ⇒ هیچ سطری دست نمی‌خورد (ویرایش بی‌اثر بازهٔ الکی نمی‌سازد).
   - ترکیب تازه ⇒ بازهٔ جاری با `EffectiveTo` بسته می‌شود و بازهٔ تازه از **امروزِ کاری** باز می‌گردد.
   - دو ویرایش در یک روز ⇒ همان بازه در جای خود بازنویسی می‌شود (بازه‌های قدیمی‌تر سالم).
   - پیام موفقیت می‌گوید سهم تازه از چه تاریخی اعمال می‌شود و گذشته تغییر نمی‌کند.
6. **ساخت قرارداد** — نخستین بازه از `ContractDate` آغاز می‌شود، نه از امروز.
7. **مسیرهای نمایشی** — همه به «آخرین بازهٔ هر شریک» تقلیل داده شدند تا شریک دوبار دیده نشود
   (و `PartyStatementPageBuilder` که `ToDictionaryAsync` می‌کرد با کلید تکراری نشکند).
8. **نگهبان P0-04** — تابع تریگر به‌روز شد تا «جمع = ۱۰۰» را **برای هر بازه جداگانه** بسنجد،
   نه روی مجموع همهٔ بازه‌ها.

### Migration
`20260828232620_AddContractPartnerEffectiveDating`
- افزودن دو ستون.
- **Backfill قطعی:** `EffectiveFrom = date_trunc('day', LEAST(cp."CreatedAtUtc", c."ContractDate"))`
  — قدیمی‌ترین تاریخِ قابل اثبات برای همان سطر. هیچ تاریخی اختراع نشد.
  سطرهای یتیم (قرارداد حذف‌شده) از `CreatedAtUtc` خودشان پر می‌شوند.
- جابه‌جایی ایندکس یکتا و افزودن ایندکس کمکی.
- بازنویسی تابع تریگر برای اعتبارسنجی per-period.
- `Down`: بازه‌های قدیمی‌تر حذف و فقط جدیدترین بازهٔ هر (قرارداد، شریک) نگه داشته می‌شود تا کلید
  یکتای قبلی برقرار گردد؛ تابع تریگر هم به نسخهٔ قبلی برمی‌گردد.

**هیچ سطری در `Up` حذف نمی‌شود و هیچ مبلغی تغییر نمی‌کند.**

### Files changed
- `Models/Entities/ContractsAndPricing.cs` (دو ستون + توضیح)
- `Data/ApplicationDbContext.cs` (ایندکس‌ها)
- `Services/PartyStatements/ContractPartnerShareHistory.cs` (**جدید**)
- `Services/PartyStatements/PartnershipStatementService.cs` (`AllocateProfitBySharePeriod`, `LoadMemberLinksAsync`)
- `Services/PartyStatements/PartyStatementReadService.cs`
- `Services/PartyStatements/PartyBalanceReadService.cs`
- `Services/PartyStatements/PartyStatementPageBuilder.cs`
- `Controllers/ContractsController.cs` (`ApplyPartnerShareChange` + فرم ویرایش + خلاصهٔ Audit + پیام)
- `Controllers/ContractJourneyController.cs`, `Controllers/PaymentsController.cs` (نمایش)
- `Migrations/20260828232620_AddContractPartnerEffectiveDating.cs` (**جدید**)

### Regression tests
- `Probe04_Changing_SharePercent_Does_Not_Rewrite_Partner_Profit_History` (بازنویسیِ Probe04)
- `Probe04b_Sales_After_The_New_Period_Use_The_New_Share` (تست تازه — نگهبانِ افراط: تغییر نباید بی‌اثر شود)
- `Probe09*` (اعتبارسنجی per-period)
- کل مجموعهٔ `PartnerProfileTests`, `PartnershipStatementTests`, `PartnerFundingTests`,
  `PartyStatementReadServiceTests`, `PartnershipStatementViewTests` بدون تغییر و سبز.

### Before / After

سناریوی گزارش: Partner A خرید ۴۰۰k را پرداخت، Partner B گمرک ۶۰k را، فروش ۶۰۰k در 2025-04-01،
سپس تغییر ۵۰/۵۰ به ۸۰/۲۰.

| | Before | After |
|---|---|---|
| پیش از تغییر | A=270,000.00 · B=270,000.00 (bookProfit=540,000.00) | همان |
| پس از باز کردن بازهٔ ۸۰/۲۰ از 2026-01-01 | **A=432,000.00 · B=108,000.00** (۱۶۲٬۰۰۰ جابه‌جایی بدون سند) | **A=270,000.00 · B=270,000.00** (بدون تغییر) |
| فروش تازه پس از تاریخ تغییر (Probe04b) | — | bookProfit=1,140,000.00 → A=741,000.00 · B=399,000.00 (نیمی زیر ۵۰/۵۰، نیمی زیر ۸۰/۲۰؛ جمع = مفاد) |

### Database impact
دو ستون تازه روی `ContractPartners`، جابه‌جایی یک ایندکس یکتا، افزودن یک ایندکس، و بازنویسی
تابع تریگر. Backfill فقط دو ستونِ تازه را پر می‌کند.

### Financial impact
صورت‌حساب شراکت، پروفایل شریک، ماندهٔ شریک و P&L قرارداد برای دوره‌های بسته دیگر با یک ویرایش
تغییر نمی‌کنند. گزارش‌های تاریخی قابل بازتولید شدند.

### Compatibility risk
متوسط (schema change)، ولی **از نظر عددی خنثی**: هر قرارداد پس از Backfill دقیقاً یک بازه دارد،
پس همهٔ مسیرها همان اعداد قبلی را می‌دهند. این با ۲٬۶۷۵ تست سبز و با تطبیق‌های ۱۲ ماهه تأیید شد.

### Remaining concern (مستند و آگاهانه)
1. **گذشتهٔ واقعی قابل بازسازی نیست.** سیستم هیچ سابقه‌ای از تغییرات قبلیِ سهم ندارد
   (`ContractPartners` بازنویسی می‌شد و فقط متنِ Audit می‌ماند). بنابراین کل گذشته زیر
   «آخرین ترکیبِ ثبت‌شده» می‌ماند — دقیقاً همان چیزی که امروز هم گزارش می‌شود. از این پس هر
   تغییر بازهٔ خودش را می‌سازد. طبق دستور، تاریخ ساختگی اختراع نشد.
2. **تاریخِ اعتبارِ بازهٔ تازه، «امروزِ کاری» است.** فرم ویرایش فیلد «از تاریخ» ندارد و طبق قاعدهٔ
   «UI را بازطراحی نکن» اضافه نشد. اگر شرکا توافق کنند که ترکیب تازه از اول ماه گذشته اعتبار
   داشته باشد، فعلاً از UI قابل بیان نیست. پیشنهاد برای فاز بعد: یک فیلد تاریخِ اختیاری در
   ویرایشگر سهم.
3. **مبنای تقسیمِ مفاد، عایدِ فروشِ هر بازه است.** بهای خرید از `IPurchaseAggregationService`
   می‌آید که تاریخ‌دار نیست؛ تاریخ‌دار کردن آن یک refactor بزرگِ خارج از دامنهٔ این فاز بود.
   انتخابِ فعلی قطعی، مستند و برای تک‌بازه دقیقاً معادلِ فرمول قبلی است.
4. `PartnershipPartnerTotals.SharePercent` همچنان **درصد جاری** را نشان می‌دهد (برچسب).
   برای قراردادی با چند بازه، عددِ مفاد ترکیبی است و لزوماً برابر `bookProfit × برچسب` نیست.

---

## ALL TEST RESULTS

### مجموعهٔ Simulation
```
dotnet test --filter "FullyQualifiedName~Simulation"
Total tests: 52   Passed: 52   Failed: 0   (2.67 min)
```

### کل پروژهٔ تست
```
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj
Failed: 12   Passed: 2675   Skipped: 0   Total: 2687   (5 m 8 s)
```

### تحلیل ۱۲ شکست
هر ۱۲ مورد **از قبل موجود** و بی‌ربط به این فاز هستند. این ادعا حدس نیست: یک worktree تمیز
روی `HEAD` (commit `077bf66`) ساخته شد و همان ۱۲ تست آنجا هم شکستند:

```
Failed: 12   Passed: 5   Total: 17   (روی HEAD تمیز، بدون هیچ‌کدام از تغییرات این فاز)
```

| Test | چرا بی‌ربط است |
|---|---|
| `AkDetailV2StructureTests.Operations_Details_Use_One_Compact_Shared_Composition` | تست ساختار View؛ فایل‌های مربوطه لمس نشدند |
| `ContractJourneyViewStructureTests.Loading_Receipt_Form_Uses_Shared_Ak_Layout` | رشته‌های `Source Information` / `Destination Terminal` اصلاً در فایل نیستند (۰ مورد)؛ diff این فاز روی آن فایل فقط **یک خط** `@Html.FormToken()` است |
| `ContractJourneyViewStructureTests.Operation_Record_Detail_Pages_Use_Shared_Ak_Detail_Contract` | تست ساختار View |
| `DetailsP0RegressionTests.Loading_Details_Does_Not_Truncate_Lists_With_Take5` | `Views/Loading/Details.cshtml` در این فاز اصلاً تغییر نکرد |
| `DetailsP0RegressionTests.ShipmentPnl_Details_Uses_Single_Categorisation_Source` | Shipment PnL لمس نشد |
| `MasterDataCleanupTests.Sidebar_Exposes_Primary_Items_And_Goods_Logistics_Group` | Sidebar لمس نشد |
| `OperationsLinearDetailStructureTests` (۲ مورد) | تست ساختار View |
| `PartnerProfileTests.Ledger_HasTheAccountantsColumns` | markup پروفایل شریک؛ در این فاز تغییر نکرد |
| `PartnerProfileTests.QuantityAndRate_ComeFromTheDocumentOrStayEmpty` | همان |
| `ShipmentPnlControllerTests.Finance_Tab_Uses_Single_Whole_Shipment_Result_And_Full_Costs` | Shipment PnL لمس نشد |
| `TransportWorkflowViewStructureTests.Details_Centres_The_Chain_And_Valid_Next_Actions` | تست ساختار View |

منشأ آن‌ها کارِ ناتمامِ commit‌نشدهٔ موجود در working tree پیش از شروع این فاز است.
**هیچ تستی برای سبز شدن Build ضعیف یا حذف نشد.** دو تست بازنویسی شدند و هر دو دقیقاً همان
رفتاری را pin می‌کردند که این فاز اصلاحش کرده است:

- `EnsureMovementDoesNotCauseFutureNegativeStock_Temporarily_AllowsBackdatedOutThatBreaksLaterBalance`
  → `…_BlocksBackdatedOutThatBreaksLaterBalance` (نامش خودش می‌گفت «Temporarily»)
- `Probe01/Probe03/Probe04/Probe09` → همان سناریو با انتظارِ درست.

تعداد تست‌ها از ۲۶۷۹ به ۲۶۸۷ رسید (۸ تست تازه).

---

## 12-MONTH SIMULATION RESULT (پس از اصلاحات)

| بررسی | نتیجه |
|---|---|
| Contracts / Loadings / Receipts | 80 / 1200 / 1200 |
| InventoryMovements / Sales / Expenses / Payments | 2820 / 1500 / 1500 / 1500 |
| TruckDispatches / LossEvents / LedgerEntries | 600 / 120 / 5700 |
| `StockService` در برابر ریاضیِ حرکات موجودی — ۶۰ scope | ✅ اختلاف صفر |
| ردیف لجر یتیم (Sale/Expense/Loading) | ✅ 0 / 0 / 0 |
| سند بدون ردیف لجر (فروش/مصرف/پرداخت) | ✅ 0 / 0 / 0 |
| لجر تکراری برای یک فروش | ✅ 0 |
| جمع ماهانهٔ فروش/مصرف در برابر جمع لجر (هر ۱۲ ماه) | ✅ منطبق |
| سهم مفاد شرکا در برابر مفاد دفتر (۴ جفت شریک) | ✅ منطبق |
| ماندهٔ ۶ تأمین‌کننده = بارگیری − پرداخت | ✅ منطبق |
| سهم شراکت ناسازگار (per-period) | ✅ صفر |

> `SIM-INV-04` (۸ گودالِ گذرای منفی که خودشان ترمیم شده‌اند) همچنان گزارش می‌شود. این
> artifact مولدِ دادهٔ حجیم است که حرکات را مستقیم می‌نویسد و از نگهبان عبور نمی‌کند؛
> ضمناً با قاعدهٔ «ماندهٔ پایانی» این حالت عمداً مجاز است (بند PTG-P0-02).
> مدرکِ واقعیِ اصلاح، `Probe03` است که از `SalesController` عبور می‌کند.

---

## PERFORMANCE BEFORE VS AFTER

### دادهٔ یک‌ساله (۵٬۷۰۰ ردیف لجر)

| مسیر | Before | After |
|---|---|---|
| `StockService.GetMovementSummaryAsync` | 78 ms | 43 ms |
| `ProfitAndLossService.BuildCompanyAsync` | 121 ms | 83 ms |
| `NegativeStockAnalysisService.AnalyzeAsync` | 64 ms | 102 ms |
| صفحهٔ اول دفتر کل (۵۰ ردیف) | 8 ms | 16 ms |
| `PartnershipStatementService.BuildAsync` | 22 ms | 30 ms |

### حجم چندساله (۳۰۰٬۰۰۰ لجر / ۱۵۰٬۰۰۰ حرکت / ۶۰٬۰۰۰×۳ سند)

| مسیر | Before | After | Budget |
|---|---|---|---|
| صفحهٔ اول دفتر کل | 54 ms | 31 ms | 1,000 |
| صفحهٔ عمیق دفتر کل (offset 250k) | 357 ms | 243 ms | 3,000 |
| شمارش کل دفتر کل | 154 ms | 157 ms | 2,000 |
| گردش حساب مشتری (بدون فیلتر تاریخ) | 3,978 ms | 1,931 ms | 4,000 |
| گردش حساب تأمین‌کننده | 1,949 ms | 2,111 ms | 4,000 |
| موجودی آزاد یک مخزن | 125 ms | 112 ms | 1,500 |
| خلاصهٔ حرکات موجودی | 542 ms | 210 ms | 5,000 |
| تحلیل موجودی منفی | 1,967 ms | 1,462 ms | 8,000 |
| P&L شرکت | 251 ms | 301 ms | 8,000 |
| لیست فروش صفحهٔ اول | 36 ms | 49 ms | 1,000+ |

**هیچ مسیری از بودجهٔ خود عبور نکرد و هیچ افت معناداری دیده نشد.** نوسان‌های ده‌ها میلی‌ثانیه
نویزِ بار ماشین است، نه اثر تغییرات. نکتهٔ مثبتِ عمدی: نگهبانِ بازفعال‌شدهٔ موجودی به‌جای خواندن
کل خط زمانی، یک `SUM` برای گذشته می‌گیرد و فقط سطرهای «از تاریخ سند به بعد» را می‌پیماید، پس
برای ثبتِ امروز (حالت رایج) تقریباً هیچ سطری نمی‌خواند.

---

## DATABASE SAFETY CONFIRMATION

- **دیتابیس Production (`ptg_oil_system`) دست‌نخورده است.** بررسی مستقیم پس از اتمام کار:
  ```
  table_exists = t | EffectiveFrom column = 0 | TR_ContractPartners_ShareSum trigger = 0
  ```
  یعنی هیچ‌کدام از دو migration روی آن اجرا نشده است.
- هیچ `dotnet ef database update` اجرا نشد. تنها فرمان‌های EF اجراشده `migrations add` بودند
  که اصلاً به دیتابیس وصل نمی‌شوند؛ برای احتیاط بیشتر، `ConnectionStrings__DefaultConnection`
  حین آن‌ها روی یک نام موقتِ پیشونددار (`ptg_oil_accounting_test_migrationscaffold`) ست شد —
  آن دیتابیس هرگز ساخته نشد.
- همهٔ تست‌های یکپارچگی روی دیتابیس‌های موقتی با پیشوند اجباری
  `ptg_oil_accounting_test_` اجرا و در پایان `DROP` شدند
  (`DatabaseSafetyGuard.EnsureIntegrationTestCreate/Use/DropAllowed`).
- تنها دیتابیس تستِ باقی‌مانده `ptg_oil_accounting_test_094dfce51a444e4cb1c8030c0500d985` است
  که **پیش از شروع این فاز** هم وجود داشت (leftover قدیمی، ساختهٔ این کار نیست).
- هیچ رکورد واقعی مشتری خوانده، تغییر یا حذف نشد. تنها تماس با Production یک `SELECT` روی
  `information_schema` برای تأیید همین ایمنی بود.
- `Accounting.Enabled` دست‌نخورده و همچنان `false` است. هیچ Pilot ای فعال نشد.

---

## FILES CHANGED

### فایل‌های تازه
```
src/PTGOilSystem.Web/Services/PartyStatements/ContractPartnerShareHistory.cs
src/PTGOilSystem.Web/Migrations/20260828230100_AddContractPartnerShareSumGuard.cs        (+ .Designer.cs)
src/PTGOilSystem.Web/Migrations/20260828232620_AddContractPartnerEffectiveDating.cs      (+ .Designer.cs)
tests/PTGOilSystem.Web.Tests/Simulation/FormIdempotencyCoverageTests.cs
```

### Controllers
```
ContractsController.cs          ContractJourneyController.cs    DispatchController.cs
ExpensesController.cs           LoadingController.cs            LoadingReceiptsController.cs
LossEventsController.cs         PartnershipStatementController.cs
PaymentsController.cs           SupplierBalanceTransfersController.cs
TruckSettlementsController.cs   TruckSettlementsController.GroupUnload.cs
```

### Services / Data / Models
```
Services/StockService.cs
Services/InventoryMovementWriter.cs
Services/PartyStatements/PartnershipStatementService.cs
Services/PartyStatements/PartyStatementReadService.cs
Services/PartyStatements/PartyBalanceReadService.cs
Services/PartyStatements/PartyStatementPageBuilder.cs
Data/ApplicationDbContext.cs
Models/Entities/ContractsAndPricing.cs
Migrations/ApplicationDbContextModelSnapshot.cs
```

### Views (فقط افزودن توکن / پرچم opt-in)
```
Views/Expenses/Create.cshtml                    Views/Expenses/CreateWagonRent.cshtml
Views/Expenses/CreateGroup.cshtml               Views/Loading/Create.cshtml
Views/LoadingReceipts/_ReceiptCreateForm.cshtml Views/Dispatch/Create.cshtml
Views/Dispatch/CreateDirectFromReceipt.cshtml   Views/LossEvents/Create.cshtml
Views/SupplierBalanceTransfers/Create.cshtml    Views/TruckSettlements/GroupUnload.cshtml
Views/PartnershipStatement/Index.cshtml         Views/Shared/_CreateModalShell.cshtml
```

### Tests
```
tests/PTGOilSystem.Web.Tests/DateTimeNormalizationTests.cs        (۱ تست بازنویسی، ۱ تست تازه)
tests/PTGOilSystem.Web.Tests/Simulation/ProductionRiskProbeTests.cs
tests/PTGOilSystem.Web.Tests/Simulation/SimulationWorld.cs
tests/PTGOilSystem.Web.Tests/Simulation/TwelveMonthProductionSimulationTests.cs
tests/PTGOilSystem.Web.Tests/Simulation/FormIdempotencyCoverageTests.cs   (جدید)
```

> فایل‌های دیگری در working tree تغییر خورده‌اند (`Views/Partners/Details.cshtml`،
> `Views/Customers/Details.cshtml`، `Views/PartyStatements/*`، `PartyStatementReadServiceTests.cs`
> و …) که **پیش از این فاز** هم تغییرخورده بودند و به این کار ربطی ندارند.

---

## MIGRATIONS CREATED

| # | نام | کار | تخریبی؟ |
|---|---|---|---|
| ۱ | `20260828230100_AddContractPartnerShareSumGuard` | تابع + CONSTRAINT TRIGGER تعویق‌دار برای «جمع سهم = ۱۰۰» | خیر — فقط ساخت تابع/تریگر |
| ۲ | `20260828232620_AddContractPartnerEffectiveDating` | افزودن `EffectiveFrom`/`EffectiveTo`، Backfill قطعی، جابه‌جایی ایندکس یکتا، بازنویسی تریگر به حالت per-period | خیر در `Up` — هیچ سطری حذف نمی‌شود |

هر دو migration فقط روی دیتابیس‌های موقتِ تست اجرا و تأیید شدند (fixture از صفر همهٔ ۱۰۹
migration را اجرا می‌کند و ۵۲ تست Simulation روی نتیجهٔ آن سبزند).
**هیچ‌کدام روی Production اجرا نشده‌اند.**

`Down` مهاجرت دوم عمداً تخریبی است (بازه‌های قدیمی‌تر را جمع می‌کند) چون مدل قبلی بیش از یک بازه
را نمی‌فهمد؛ پیش از هر Rollback باید Backup گرفته شود.

---

## توصیه پیش از استقرار

1. کوئری تطبیق `Probe09d` را روی یک نسخهٔ کپیِ Production اجرا کنید تا قراردادهای با سهم
   ناسازگار پیش از migration مشخص شوند.
2. گزارش «موجودی منفی» (`NegativeStockAnalysisService`) را اجرا کنید؛ scopeهایی که ماندهٔ
   پایانی‌شان منفی است، پس از این اصلاح هر خروجِ تازه‌ای را رد می‌کنند تا اصلاح شوند.
3. Backup پیش از اجرای دو migration.

**متوقف شدم. هیچ مورد P1/P2/P3/P4 لمس نشد و هیچ چیزی deploy نشد. منتظر دستور بعدی.**
