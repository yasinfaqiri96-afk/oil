# UI DESIGN SYSTEM — `ak-*`

> قرارداد فعال رابط پروژه؛ مقادیر اجرایی از `01-tokens.css` و ترکیب نهایی از `78-mashal-reference.css` می‌آیند.
> صفحهٔ کامل و سند مودال از همان primitives استفاده می‌کنند. استثناهای چاپ، chrome و ترکیب workflow مجازند؛ کنترل موازی مجاز نیست.
> **قاعدهٔ اول:** برای صفحهٔ جدید هیچ CSS صفحه‌ای، skin، variant یا `!important` جدید نساز. از کامپوننت‌های زیر استفاده کن.

## توکن‌ها

منبع واحد: `wwwroot/css/ptg/01-tokens.css`.

| نقش | مقدار |
|---|---|
| پس‌زمینهٔ بدنه | `#F5F6FA` |
| متن | `#343a40` |
| آبی ناوبری/تعاملی | `#2d60c9` / hover `#2453b4` |
| اکشن اصلی مشترک | `var(--primary-main)` / hover `var(--primary-dark)` |
| سایدبار فشرده | همان آبی سرمه‌ای پنل (۸۸px) |
| پنل سایدبار | `#1A467D` (۲۴۰px در پوستهٔ فعال)؛ متن `#DCE6F4` و آیکن `#D2E7FF` |
| موفق | `#E7F5E9` / `#23713B` |
| خطا | `#FFEAF0` / `#B00A0A` |
| هشدار | `#FDF1E3` / `#8A5200` |
| اطلاع | `#E8F1FF` / `#0D52AF` |
| چارت | ورودی `#8BB475` · خروجی `#FB7185` · سود `#7779A2` |

تایپوگرافی: IRANSans با fallback از Vazirmatn، RTL کامل، اعداد مالی LTR + tabular.
عنوان صفحه ۲۰px/۵۰۰ · عنوان سکشن ۱۶px/۵۰۰ · هدر و سلول جدول ۱۴px. کنترل موبایل ۱۶px برای جلوگیری از zoom خودکار.
کانتینر از `--layout-content-max` و breakpointهای پوسته می‌آید. شعاع کنترل ۳، دکمه ۴ و پنل ۶ پیکسل است. موشن ۱۴۰–۱۹۰ms. Shadow فقط Dropdown/Tooltip/Modal.
سبک: flat — بدون Card تزئینی، بدون Shadow تزئینی، بدون گرادیان تزئینی.

## کامپوننت‌های مشترک Razor

مسیر: `Views/Shared/Components/Ak/`

| کامپوننت | کاربرد |
|---|---|
| `_AkPageHeader` | هدر صفحه: عنوان + اکشن اصلی آبی + منوی سه‌نقطه |
| `_AkSectionHead` | عنوان سکشن + توضیح + divider |
| `_AkSearchFilter` | **تنها** Search/Filter پروژه (بخش زیر) — مسیر: `Views/Shared/_AkSearchFilter.cshtml` |
| `_AkFooterActions` | فوتر فرم: Cancel خنثی + Save آبی |

کامپوننت‌های مشترک دیگر که همچنان زنده‌اند: `_DetailsTabs` (ریل `ptg-tabs-rail` + `details-tabs.js`)، `_PagedListFooter`، `_ToggleActiveButton`، `_PartyStatementTable`، `_SupplierStatementLedger`، `_ReferenceMetricCard`.

TagHelper: `AkEntityComboboxTagHelper` (+ `wwwroot/js/ak-entity-combobox.js`) — کمبوی انتخاب موجودیت با «ایجاد سریع» و بازگشت از طریق `returnUrl`.

## کلاس‌های CSS

منبع ساختاری: `wwwroot/css/ptg/50-ak-components.css` (بدون `!important`).
قواعد عمومی/توکن: `wwwroot/css/ptg/45-akaunting.css`.

| خانواده | کلاس‌ها |
|---|---|
| صفحه | `ak-form-page` · `ak-list-page` · `ak-party-page` |
| فرم | `ak-form` · `ak-form-section` · `ak-form-grid` · `ak-field` · `ak-label` · `ak-input` · `ak-input-unit` · `ak-field-error` · `ak-footer-actions` |
| جدول | `ak-table` · `ak-table-wrap` · `ak-col-grow` · `ak-col-num` · `ak-col-check` · `ak-col-actions` · `ak-num` · `ak-name` |
| لیست/خلاصه | `ak-list` · `ak-list-row` · `ak-summary` · `ak-summary-list` · `ak-subrow` · `ak-row-muted` |
| وضعیت و اکشن | `ak-status` · `ak-row-menu` · `ak-empty` · `ak-pager` |
| Search/Filter (مالک واحد) | `ak-filter-host` · `ak-filter` · `ak-filter-chips` · `ak-chip` · `ak-filter-input` · `ak-filter-enter` · `ak-filter-clear` · `ak-filter-popover` — تعریف در `45-akaunting.css` |
| تولبار (غیر Search/Filter) | `ak-report-parameters` · `ak-detail-toolbar` · `ak-subform-toolbar` (بخش «تولبارها») |
| داشبورد | `ak-hub-grid` · `ak-hub-tile` |
| مودال | `ak-page-modal` (+ حالت `is-compact`) |
| تب | `ptg-tabs-rail` · `ptg-tab-item` (canonical، پیش از ak هم بود) |

## Search / Filter — مالک واحد

کل Search و Filter پروژه دقیقاً چهار فایل است. جای دیگری Search/Filter ساخته نمی‌شود:

| فایل | نقش |
|---|---|
| `Views/Shared/_AkSearchFilter.cshtml` | تنها markup Search/Filter (chips + input + popover) |
| `Models/AkSearchFilterModel.cs` | مدل: `SearchName`/`SearchValue`/`Placeholder`/`Filters`/`Hidden` (+ `AkFilterDefinition`, `AkFilterOption`) |
| `wwwroot/js/ak-search-filter.js` | تنها JS این کامپوننت (popover، chips، clear) |
| بخش Search/Filter در `wwwroot/css/ptg/45-akaunting.css` | تنها CSS این کامپوننت |

### رفتار قطعی

- **جستجوی آزاد server-side است**: `<form method="get">` معمولی؛ نتیجه از سرور می‌آید.
- **انواع فیلتر پشتیبانی‌شده**: `select`، `bool`، `date`، `daterange`، `text`. هر فیلتر ۱:۱ به یک پارامتر query موجود نگاشت می‌شود (`Key`، و برای بازه `SecondKey`).
- **query string منبع حقیقت است.** وضعیت فیلتر از View خوانده و به‌صورت server-rendered رندر می‌شود؛ state پنهان در JS/localStorage وجود ندارد.
- **chipها server-rendered** هستند (`ak-chip-group` به‌ازای هر فیلتر اعمال‌شده) و مقدارشان با `<input type="hidden">` واقعاً submit می‌شود.
- **hidden passthrough**: پارامترهای scope (مثل `contractId`) از طریق `Hidden` رد می‌شوند و در search/filter/clear حفظ می‌مانند (chip نمی‌شوند).
- **حذف یک chip** = حذف پارامتر آن + submit مجدد. **Clear All** = پاک‌کردن همهٔ chipها و متن جستجو، با حفظ `Hidden`.
- **sorting و pagination دست‌نخورده‌اند**: کامپوننت آن‌ها را نمی‌شناسد و مصرف نمی‌کند.
- **RTL کامل** (منطق `inset-inline-*`).
- **SPA lifecycle**: init روی `ptg:page-ready` تکرارپذیر است و listener تکراری ثبت نمی‌کند.
- **visibility فقط با صفت `hidden`** کنترل می‌شود (popover و دکمهٔ clear).
- **فیلتر اصلی و قابل‌اشتراک server-side است.** `list-grid.js` فقط برای جدول کاملِ بدون pagination سرور، فیلتر نمایش محلی دارد. این فیلتر scope مستقل، وضعیت آشکار و Clear دارد؛ در موبایل نیز قابل‌دسترسی است. خروجی بر اساس فیلترهای اصلی صفحه تهیه می‌شود، نه محدودیت نمایشی محلی.

### وضعیت مهاجرت (۲۰۲۶-۰۷-۱۴)

- **۴۳ صفحه** `[data-ak-filter]` دارند؛ تمام Search/Filterهای واقعی به همین مالک مشترک مهاجرت کرده‌اند.
- Search/Filter اصلی مالک مشترک دارد؛ فیلتر ستونی نمایش در `list-grid.js` قرارداد جدا و صریح دارد و جای آن را نمی‌گیرد.
- `wwwroot/js/ak-filter.js` **حذف شد** و reference آن از `_Layout.cshtml` **حذف شد**.
- CSS legacy (`.ak-fbar*`، `.ak-token*`، `.ak-fpop*`، `.ak-filterbar`، `.ak-search`، `.ak-search-input`، `.ak-filter-pop`، `.ak-filter-toggle`، `.ak-filter-field`، `.ak-filter-apply`) از `50-ak-components.css` **حذف شد**.

### محدودیت‌های شناخته‌شده (عمدی)

- operatorهای نمایشی `≠`، `∈`، `↔` پیاده نشده‌اند؛ backend فعلی آن‌ها را پشتیبانی نمی‌کند.
- multi-select ساخته نشده؛ قرارداد چندمقداری در backend وجود ندارد.
- باگ `DateTime Kind=Unspecified → timestamptz` یک مشکل شناخته‌شدهٔ **server-side** است و خارج از مهاجرت UI؛ در این مهاجرت دست نخورد.

### قانون نگهداری

- Search/Filter جدید **فقط** با `_AkSearchFilter` ساخته می‌شود.
- ساخت partial، CSS یا JavaScript **موازی** برای Search/Filter ممنوع است.
- استفادهٔ دوباره از نام `.ak-filterbar` برای Search/Filter ممنوع است.
- فیلتر جدید فقط وقتی اضافه می‌شود که backend واقعاً پارامتر آن را پشتیبانی کند.

## تولبارها — این‌ها Search/Filter نیستند

سه خانوادهٔ زیر فقط ردیف اکشن/پارامترند. **نباید** با `_AkSearchFilter` ادغام شوند و **نباید** به‌عنوان Search/Filter استفاده شوند:

| کلاس | کاربرد | زیرکلاس‌ها |
|---|---|---|
| `ak-report-parameters` | پنل پارامتر گزارش‌ها (`Reports/*`): دکمهٔ قیف + popover پارامترهای GET | `-bar` · `-anchor` · `-toggle` · `-pop` · `-field` · `-apply` |
| `ak-detail-toolbar` | صفحات Details و عملیات: انتخاب دوره/قرارداد، چاپ/خروجی، اکشن گروهی، چیپ نوع | `-actions` · `-search` · `-chip` · `-start` |
| `ak-subform-toolbar` | زیرفرم‌ها و CreateGroup (مصرف/فروش گروهی، وارد کردن اکسل وسایط) | `-search` |

تعریف هر سه در `50-ak-components.css`.

## ترتیب بارگذاری CSS در `_Layout`

فهرست زیر ترتیب referenceهای توسعه است؛ production از bundle با همان ترتیب استفاده می‌کند. فایل‌های شرطی فقط در context مربوط بارگذاری می‌شوند.

```text
01-tokens.css
→ 02-base.css
→ 03-layout.css
→ 04-sidebar.css
→ 05-components.css
→ 06-forms.css
→ 07-tables.css
→ 08-modals.css
→ 09-pages.css
→ 10-responsive.css
→ 11-details.css
→ 12-dashboard.css
→ 13-compat.css
→ 14-master-details.css
→ 15-system-lists.css
→ 17-system-forms.css
→ 18-toasts.css
→ 40-motion.css
→ 63-party-details.css
→ 16-system-tabs.css
→ 41-compact.css
→ 45-akaunting.css
→ 50-ak-components.css
→ 65-excel-import.css
→ 52-stat-card.css
→ 61-finance-workspace.css
→ 67-journal-table.css
→ 70-page-frame.css
→ 71-typography.css
→ 72-surfaces.css
→ 73-detail-system.css
→ 74-header-controls.css
→ 76-form-system.css
→ 66-backups.css
→ 80-assistant.css
→ 78-mashal-reference.css
→ 81-onboarding.css
→ 60-loader.css
```

مودال chrome مستقل دارد، اما `76-form-system` و `78-mashal-reference` و scope محتوای مشترک را نیز بارگذاری می‌کند.
CSS صفحه باید `data-ptg-page-asset` داشته باشد؛ فایل و initializer مشترک یک فرم در Create و Edit یکسان استفاده شوند.

## قواعد عملی (زخم‌خورده)

- **CSS صفحه‌ای باید page-asset باشد.** ناوبری SPA چرخهٔ CSS پایه را رد می‌کند؛ CSS مخصوص صفحه باید با `data-ptg-page-asset` و پرچم آن بیرون از `renderShellChrome` تعریف شود، وگرنه صفحه بی‌استایل می‌آید.
- **`@section Styles` از ناوبری SPA جان سالم به‌در نمی‌برد.** `<style>` درون صفحه بعد از کلیک روی لینک SPA برمی‌گردد؛ CSS صفحه را در فایل سراسری با کلاس صفحه scope کنید.
- **فرم POST به اکشن `[HttpPost]`-only** باید `data-no-spa` بگیرد، وگرنه fallback‌ـه spa-nav به GET صفحهٔ خالی ۴۰۵ می‌دهد.
- **هر فرم POST باید `@Html.AntiForgeryToken()` صریح داشته باشد** — auto-inject خاموش است؛ نبودش یعنی ۴۰۰ خاموش («هیچ اتفاقی نمی‌افتد»).
- منوی `ak-row-menu` داخل `ak-table-wrap` (که `overflow-x:auto` دارد) بریده می‌شود؛ `tables.js` برای این حالت `data-bs-strategy="fixed"` ست می‌کند.

## خانواده‌های Legacy — استفاده ممنوع

این کلاس‌ها حذف شده‌اند و نباید برگردند:
`sd-*` · `od-*` · `pp-*` / `party-*` · `cj-*` / `journey-*` / `lifecycle-*` · `ulist-*` · `ds-form-shell` · `app-form-card` · `ptg-card` · `ptg-master-*` · `status-badge` · `report-*` · `chortke-*`

### Search/Filter منسوخ — حذف‌شده، بازتولید ممنوع

| منسوخ | وضعیت |
|---|---|
| `wwwroot/js/ak-filter.js` | فایل حذف شد؛ reference از `_Layout` حذف شد |
| `.ak-fbar*` · `.ak-fpop*` · `.ak-token*` | CSS حذف شد (نوار توکنی client-side) |
| `.ak-filterbar` به‌عنوان Search/Filter | حذف شد؛ نام آن دیگر برای Search/Filter استفاده نمی‌شود |
| `.ak-search` · `.ak-search-input` · `.ak-filter-pop` · `.ak-filter-toggle` | CSS حذف شد |
| `_AkSearchBar` | حذف شد؛ جایگزین: `_AkSearchFilter` |
| `_ManagementFilterBar` | حذف شد؛ جایگزین: `_AkSearchFilter` |
| dropdown فیلتر Bootstrap قدیمی (`data-bs-toggle="dropdown"` روی نوار فیلتر لیست) | حذف شد؛ popover مالک مشترک جایگزین است |
