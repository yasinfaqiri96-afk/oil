# PTG UI/UX Baseline Audit

این سند snapshot مرحله Setup در 2026-08-03 است. برای قواعد دائمی از `MASTER.md` استفاده شود.

## وضعیت و دامنه

- Repository root: `F:\New folder\saddiqi-group\oil`
- Solution: `ptg-oil-system.sln`
- برنامه اصلی: `src/PTGOilSystem.Web`
- Stack: ASP.NET Core MVC / .NET 8، Razor، Bootstrap 5 RTL، JavaScript بدون framework
- Git از قبل dirty و branch محلی یک commit جلوتر از `origin/main` بود.
- در Setup هیچ View، CSS، JavaScript، Controller، Model، database، migration، secret، upload یا production file تغییر نکرد.

## موجودی UI

- 370 فایل Razor
- 43 صفحه `Details`
- 44 صفحه `Create`
- 30 صفحه `Edit`
- 62 صفحه `Index`
- 44 فایل CSS در `wwwroot/css`
- 37 فایل JavaScript کاربردی/مستند در `wwwroot/js`

## هویت معتبر موجود

### Shell

`Views/Shared/_Layout.cshtml` مالک shell، Sidebar، Topbar، navigation tree، search dialog، language، fiscal year، SPA asset flags و ترتیب واقعی CSS/JS است.

### خانواده AK

الگوی غالب و قابل توسعه:

- `_AkPageHeader`
- `_AkSectionHead`
- `_AkFooterActions`
- `_AkSearchFilter`
- `StatCard`
- `_DetailKpiStrip`
- `_DetailSummaryCard`
- `_DetailsTabs`
- `_OperationsDetailMore`
- `_DetailActionBar`
- `_ExportMenu`
- `AkEntityComboboxTagHelper`

### CSS source of truth

`01-tokens.css` مرجع توکن runtime است. `45-akaunting.css` و `50-ak-components.css` قرارداد عمومی و ساختاری AK را می‌سازند. `70–74` لایه‌های جدیدتر قاب، typography، surface، Details و header controls هستند.

ترتیب واقعی بارگذاری در `_Layout.cshtml`:

1. Bootstrap / Bootstrap RTL و Bootstrap Icons
2. `_variables.css`, `_utilities.css`, `site.css`, `boltz-shell.css`
3. `ptg/01` تا `18` و `40-motion`
4. برای authenticated shell: `63`, `16`, `41`, `45`, `50`, `65`, `52`, `61`, `64`, `70–74`, `66`
5. page assets شرطی و JavaScriptهای shared

این ترتیب با بخش قدیمی ترتیب CSS در `docs/UI-DESIGN-SYSTEM.md` یکسان نیست؛ برای تصمیم آینده `_Layout.cshtml` مرجع است.

## صفحات نماینده بررسی‌شده

| نوع | مرجع | نتیجه |
|---|---|---|
| Dashboard | `Views/Home/Index.cshtml` | چهار KPI اصلی، دو visualization و خلاصه عملیاتی؛ الگوی معتبر dashboard |
| List | `Views/Sales/Index.cshtml`, `Contracts/Index.cshtml`, `Loading/Index.cshtml` | PageHeader + KPI + AkSearchFilter + table + pager |
| Create/Edit | `Views/Sales/Create.cshtml` | فرم گروه‌بندی‌شده، SectionHead، table line items و FooterActions |
| Complex form | `Views/Loading/Create.cshtml` | workbook پرچگالی؛ رفتارهای import/validation باید حفظ شوند |
| Details | `Views/Sales/Details.cshtml`, `Payments/Details.cshtml` | AK Detail v2، KPI، summary، linked records و actions |
| Reports | `Views/Reports/Index.cshtml`, `CompanyOverview.cshtml` | hub تب‌دار + report با filter، KPI، table و export |
| Contracts | `Views/Contracts/Index.cshtml`, `ContractJourney/Details.cshtml` | list canonical؛ journey صفحه پیچیده tab/partial-driven |
| Payments | `Views/Payments/Index.cshtml`, `Create.cshtml`, `Details.cshtml` | تراکم مالی، چند mode و allocation؛ تغییر ظاهری باید semantics را حفظ کند |
| Shipment | `Views/ShipmentPnl/Details.cshtml` | جزئیات چندتب، KPIهای tab-aware، record tables و modalهای عملیاتی |

## الگوهای معتبر

- Header مشترک و hierarchy اکشن
- KPI مشترک با real data
- table/list با identity، عدد tabular، status و row menu
- Search/Filter واحد و server-side
- فرم‌های sectioned با validation نزدیک فیلد
- AK Detail v2 و disclosure برای metadata
- tab rail مشترک و سازگار با RTL
- lifecycle مشترک `ptg:page-ready`
- reduced-motion و پرهیز از animation ردیف‌های بزرگ

## Legacy و ناسازگاری‌هایی که نباید الگو شوند

- `site.css` و `boltz-shell.css` هنوز قوانین قدیمی، مقدار hard-coded و `!important` دارند.
- `13-compat.css` و `17-system-forms.css` bridgeهای مهاجرتی‌اند؛ منبع component جدید نیستند.
- `docs/UI-DESIGN-SYSTEM.md` در بعضی اعداد قدیمی است: title `36px` و palette بنفش/سبز با runtime فعلی `30px`، navy و CTA آبی تطابق کامل ندارد.
- CSS موجود از دوره‌های مختلف لایه شده است؛ copy کردن selector قدیمی debt را تکثیر می‌کند.
- بعضی صفحات پیچیده هنوز JavaScript طولانی در خود View دارند؛ Setup حاضر آن‌ها را refactor نکرد.
- گزارش hub از کلاس‌های `rephub-*` استفاده می‌کند؛ این یک سطح موجود خاص است، نه خانواده عمومی برای صفحات جدید.
- Shipment/Contract جزئیات بسیار بزرگ‌اند؛ تب، partial و component موجود باید reuse شود و صفحه دیگری نباید ساختار آن‌ها را کورکورانه کپی کند.

## نتیجه ui-ux-pro-max

Skill سالم بود و `search.py` اجرا شد. پیشنهاد خام:

- Dark OLED
- Inter
- Minimal Single Column
- green CTA
- landing-page spacing

این پیشنهاد برای PTG رد شد، چون با light ERP، Vazirmatn، Bootstrap RTL، shell پایدار و تراکم داده ناسازگار است. فقط قواعد عمومی مفید آن—contrast، focus، reduced motion، responsive و پرهیز از emoji icon—در `MASTER.md` جذب شد.

## Validation فقط‌خواندنی Sales Details

در طراحی آینده حفظ می‌شود:

- مدل و همه مقادیر فروش/دریافت/مانده
- header، status، invoice action و kebab
- چهار KPI واقعی
- اطلاعات فروش، صورت‌حساب، منبع و تحویل
- پرداخت‌های مرتبط، یادداشت، timeline، related records و advanced metadata
- routeها، returnUrl، currency/FX semantics و linkهای جزئیات

مرتب می‌شود:

- hierarchy اطلاعات و هم‌ترازی summary/statement
- ریتم عمودی و responsive stacking
- اولویت مانده قابل دریافت و وضعیت دفتر
- action density و خوانایی عدد/واحد

Reuse الزامی:

- `_AkPageHeader`
- `_DetailKpiStrip`
- `_DetailSummaryCard`
- `_DetailEmptyState`
- `_OperationsDetailMore`
- `_DetailActionBar`
- `.ak-list`, `.ak-num`, `.ak-status`

در این Setup هیچ فایل UI صفحه Sales Details تغییر نکرد.

