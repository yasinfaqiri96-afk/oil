# ممیزی Design System فعلی PTG Oil System

تاریخ ممیزی: 2026-07-27

دامنه: `src/PTGOilSystem.Web`

نوع ممیزی: استخراج وضعیت موجود؛ این سند پیشنهاد بازطراحی نیست.

## 1. خلاصه اجرایی

Design System واقعی برنامه یک سیستم روشن، سازمانی، RTL-first و مبتنی بر Bootstrap RTL به‌همراه لایه اختصاصی `ak-*` است. هویت غالب آن از این عناصر ساخته می‌شود:

- پس‌زمینه بسیار روشن `#FCFCFC` و سطح سفید `#FFFFFF`
- Sidebar یکپارچه و تیره با رنگ اصلی `#2A2D45`
- متن اصلی خاکستری تیره `#424242`
- رنگ برند/ناوبری بنفش خاکستری `#55588B`
- رنگ CTA اصلی آبی `#1877F2`
- فونت Vazirmatn برای رابط فارسی/دری و Poppins برای رابط انگلیسی
- صفحه‌های دارای عرض کنترل‌شده، فرم‌های دو ستونه، جدول‌های تخت و KPI Cardهای دارای Illustration
- استفاده گسترده از logical properties برای RTL/LTR و جداسازی اعداد با جهت LTR

سیستم فعلی کاملاً تک‌لایه نیست. Bootstrap، `site.css`، فایل بزرگ legacy به نام
`boltz-shell.css` و 32 فایل `ptg/*.css` هم‌زمان در cascade حضور دارند. قاعده عملی
برای تشخیص ظاهر نهایی این است: توکن‌های `01-tokens.css`، سپس کامپوننت‌های
`45-akaunting.css` و `50-ak-components.css`، و در پایان اصلاح‌کننده‌های
`70-page-frame.css`، `71-typography.css` و `72-surfaces.css`.

## 2. روش و میزان پوشش

این ممیزی شامل موارد زیر است:

- سرشماری 350 فایل Razor در پوشه `Views`
- بررسی `_Layout.cshtml`، Layout مودال و اجزای Shared
- بررسی ترتیب بارگذاری CSS و اثر specificity/cascade
- بررسی Sidebar، Header، Dashboard و الگوهای List/Form/Detail
- بررسی نمونه‌های تخصصی Report، Finance، Shipment، Import و Statement
- بررسی JavaScriptهای Tabs، Tables، Filters، Header Search و Shell
- بررسی assetهای آواتار و Illustration
- اجرای برنامه با runner امن مخزن و migration خودکار خاموش
- بررسی HTTP محلی: `/` با پاسخ 302 به Login، صفحه Login با 200 و فایل توکن‌ها با 200

مقادیر این سند از source فعلی استخراج شده‌اند؛ فقط به نام token یا سند قدیمی اتکا
نشده است. فایل `docs/UI-DESIGN-SYSTEM.md` در چند مورد با runtime فعلی هم‌خوان نیست
و منبع نهایی این ممیزی محسوب نشده است.

محدودیت: برنامه محلی اجرا شد، اما هیچ Browser session قابل اتصال در محیط موجود
نبود. بنابراین ادعای pixel-by-pixel مشاهده‌شده در مرورگر یا پوشش authenticated
visual وجود ندارد. نتیجه بر markup، CSS نهایی، assetها، تست‌های ساختاری و HTTP
محلی متکی است.

## 3. معماری بصری و لایه‌های CSS

Layout در حالت فارسی `bootstrap.rtl.min.css` و در حالت انگلیسی
`bootstrap.min.css` را انتخاب می‌کند. سپس فونت‌ها، متغیرها، `site.css`،
`boltz-shell.css` و فایل‌های PTG به‌ترتیب بارگذاری می‌شوند.

برای یک صفحه authenticated معمولی، 40 stylesheet فعال می‌شود؛ حجم خام CSS
تقریباً 1,012,727 بایت در RTL و 1,012,619 بایت در LTR است. CSSهای اختصاصی بعضی
صفحات که از `@section Styles` می‌آیند در این عدد نیستند. این حجم و تعداد لایه‌ها
علت اصلی وجود override و اختلاف‌های موضعی است.

فقط چهار View دارای stylesheet صفحه‌ای رسمی هستند:

- Login
- Invoice Document
- Party Statement Document
- Create Sale From Shipment

دو View نیز `<style>` داخلی دارند و از قرارداد مرکزی فاصله گرفته‌اند:

- `Views/Currencies/_CreateForm.cshtml`
- `Views/Suppliers/Details.cshtml`

## 4. پالت رنگ واقعی

### 4.1 رنگ‌های برند و اصلی

| نقش | HEX | کاربرد فعلی |
|---|---:|---|
| Primary lighter | `#DCE2F9` | tint قدیمی/پس‌زمینه‌های بسیار نرم |
| Primary light | `#7779A2` | accent ثانویه و chart |
| Primary main | `#55588B` | لینک، tab فعال، focus/selection و هویت بنفش |
| Primary dark | `#404268` | hover/عنوان تأکیدی |
| Primary darker | `#2F3150` | حالت‌های تیره‌تر برند |
| CTA blue | `#1877F2` | Add/Create/Save و button اصلی فعلی |
| CTA blue hover | `#0B5ED7` | hover/focus دکمه اصلی |
| CTA blue tint | `#E7F0FE` | پس‌زمینه action نرم |
| White | `#FFFFFF` | surface، متن Sidebar و متن روی CTA |

نکته مهم: رنگ برند ساختاری `#55588B` است، اما CTA عملی فعلی آبی `#1877F2`
است. این دو را نباید به یک token تبدیل یا با یکدیگر جایگزین کرد.

### 4.2 Background، Surface، Border و Text

| نقش | HEX |
|---|---:|
| Page background | `#FCFCFC` |
| Surface / Card / Field | `#FFFFFF` |
| Neutral background | `#F5F7FA` |
| Soft surface رایج | `#F7F8FB` |
| Table header | `#F4F6FB` |
| Sidebar tint / selected row | `#F2F4FC` |
| Divider / row border | `#E5E7EB` |
| KPI border پایه | `#EDF0F3` |
| Field border | `#CFD1DE` |
| Field hover border | `#BCBFD2` |
| Disabled field background | `#EEF0F5` |
| Text primary | `#424242` |
| Text secondary | `#666B75` |
| Muted KPI/title | `#4E5968` |
| Muted KPI/unit | `#808B9C` |
| Placeholder | `#9AA0B5` |
| Disabled text | `#9E9E9E` |

### 4.3 Sidebar

| نقش | مقدار |
|---|---:|
| Panel | `#2A2D45` |
| Mini rail | `#22243A` |
| Label/icon/strong/accent | `#FFFFFF` |
| Divider | `rgba(255,255,255,0.08)` |
| Hover | `rgba(255,255,255,0.05)` |
| Active | `rgba(142,147,201,0.10)` |
| Logout/danger text | `#FF9A9A` |
| Logout/danger background | `rgba(255,120,120,0.12)` |

### 4.4 Status و Semantic colors

| وضعیت | رنگ اصلی/متن | پس‌زمینه نرم |
|---|---:|---:|
| Success | `#6EA152` / `#63914A` | `#F1F6EE` |
| Danger/Error | `#CC0000` / `#B80000` | `#FAE6E6` |
| Warning | `#F59E0B` / `#B87708` | `#FEF5E7` |
| Info | `#006EA6` / `#006395` | `#E6F1F6` |
| Viewed | `#4D4F7D` | `#EEEEF3` |
| Draft/Inactive | `#3B3B3B` | `#ECECEC` |

رنگ‌های trend کارت آماری:

- مثبت: `#18A957` روی `#EAF8F0`
- منفی: `#EF5350` روی `#FDEEEE`
- خنثی: `#808B9C` روی `#F0F2F4`

رنگ‌های Dashboard:

- سبز: `#22C55E`
- کهربایی: `#F5A524`
- قرمز-نارنجی: `#F4511E`
- آبی: `#1877F2`

رنگ‌های Chart مشترک:

- Incoming: `#8BB475`
- Outgoing: `#FB7185`
- Profit: `#7779A2`
- Grid: `#E5E7EB`

## 5. Typography

### 5.1 خانواده فونت

- RTL فارسی/دری: `Vazirmatn`, سپس `system-ui`, Tahoma/Arial
- LTR انگلیسی: `Poppins`, سپس `sans-serif`
- اعداد، مبلغ، وزن و نرخ: LTR، `unicode-bidi: isolate` و
  `font-variant-numeric: tabular-nums lining-nums`

وزن‌های preload شده Vazirmatn برابر 400 و 700 است؛ CSS در عمل از وزن‌های
500 و 600 نیز استفاده می‌کند که مرورگر آن‌ها را از فایل variable font یا synthesis
تأمین می‌کند.

### 5.2 مقیاس تایپوگرافی غالب

| سطح | اندازه | Line-height | Weight |
|---|---:|---:|---:|
| Page title / H1 | `30px` | `1.25` یا `38px` | `600` |
| Page title در عرض زیر 1200 | `28px` | حدود `1.25` | `600` |
| H2 | `20px` | `28px` | `600` |
| Section title | `19px` | `1.4` | `600` |
| H3 | `18px` | `26px` | `600` |
| Card title / H4 | `17px` | `1.45` یا `25px` | `600` |
| H5 | `16px` | `24px` | `600` |
| Body | `15px` | `1.65` | `400` |
| Table cell | `15px` | `1.55` | `400` |
| Field label | `14px` | `1.5` یا `20px` | `500–600` |
| Button | `14px` | `1.5` یا `24px` | `600` |
| Numeric value معمولی | `14.5px` | `1.5` | `500` |
| H6 | `14px` | `21px` | `600` |
| Table header | `13px` | `1.45` | `500` |
| Caption/helper/status | `13px` | `1.5` | `500–600` |
| Validation/helper کوچک | `12px` | `1.4–1.5` | `400–500` |
| KPI value استاندارد | `26px` | `1.2` | `600` |

### 5.3 Typography اختصاصی KPI

- Title: `clamp(11.5px, 0.95vw, 14px)`, وزن 600
- Value: `clamp(21px, 2vw, 29px)`, وزن 800، line-height 1
- Value با 8+ کاراکتر: `17–24px`
- Value با 14+ کاراکتر: `14–19px`
- Value با 18+ کاراکتر: `11–15px`
- Unit و Trend: `10–12px`
- در عرض زیر 1400، scale به `9–20px` کاهش می‌یابد.

## 6. Spacing، Grid و اندازه‌ها

### 6.1 مقیاس و gutter

| نقش | مقدار |
|---|---:|
| Page gap | `16px` |
| Card padding عمومی | `16px` |
| Gap پایه | `12px` |
| Gap بزرگ | `24px` |
| Field gap | `12px` |
| Form section gap token | `48px` |
| Form section margin واقعی | `52px` |
| Form grid column gap | `32px` |
| Form grid row gap | `24px` |
| Desktop page gutter | `40px` |
| 1200–1599 page gutter | `32px` |
| 768–1199 page gutter | `24px` |
| Mobile page gutter | `16px` |

### 6.2 عرض صفحات

- Page frame پیش‌فرض: `1200px`
- در viewport ≥1600: `1320px`
- در viewport ≥1920: `1400px`
- در viewport ≥2400: `1480px`
- فرم معمولی: حداکثر `860px`
- فرم wide یا دارای line-item table: حداکثر `1080px`
- Loading/Create table canvas: تا عرض کامل Page frame
- Group operations: حداقل canvas هدف `1520px`
- Party pages: `1440px` و در viewport بزرگ `1600px`

### 6.3 ارتفاع‌های پایه

| کامپوننت | ارتفاع |
|---|---:|
| Header desktop | `56px` |
| Header mobile | `52px` |
| Input/select استاندارد | `42px` |
| Search/filter input | `48px` |
| Button استاندارد | حداقل `36px` |
| Header search icon button | `40px` |
| Filter chip | `36px` |
| Status pill | حداقل `28px` |
| Person avatar در table | `28px` |
| Account switch avatar | `40px` |
| Account drawer avatar | `96px` |
| Search dialog field | `52px` |

## 7. Radius و Shadow

### 7.1 Radius

| نقش | مقدار واقعی غالب |
|---|---:|
| Card/Panel عمومی | `8px` |
| Input | `8px` |
| Button | `12–13px` |
| Filter popover/chip | `6px` |
| Status/Badge | `999px` |
| Avatar | دایره کامل؛ `50%` یا `9999px` |
| Modal معمولی | در cascade بین `8px` و `16px`؛ selector اختصاصی اغلب `16px` را غالب می‌کند |
| KPI Card desktop | `18px` |
| KPI Card زیر 1400 | `14px` |
| KPI Card زیر 900 | `12px` |

در tokenها `--ptg-radius-badge: 12px` تعریف شده، اما فایل نهایی typography عملاً
Badgeها را pill با radius `999px` می‌کند.

در `10-responsive.css` برای موبایل tokenهای radius به 20–24px تغییر می‌کنند،
در حالی که بسیاری از کامپوننت‌های جدید مقدار صریح 8–12px دارند. نتیجه این است
که بعضی Bootstrap card/modalها روی موبایل گردتر از کامپوننت‌های `ak-*` می‌شوند.

### 7.2 Shadow

قاعده پایه همچنان سبک و کم‌سایه است:

- Card shadow token قدیمی: `none`
- Soft shadow token قدیمی: `none`
- Panel shadow جدید:
  `0 1px 3px rgba(16,24,40,.04), 0 10px 28px rgba(16,24,40,.06)`
- Dialog:
  `0 20px 25px -5px rgba(16,24,40,.10), 0 8px 10px -6px rgba(16,24,40,.10)`
- Dropdown: سایه مشابه با opacity حدود 0.08
- KPI پایه:
  `0 4px 14px rgba(31,41,55,.045)`
- KPI hover:
  `0 6px 18px rgba(31,41,55,.06)`

`72-surfaces.css` panel shadow را به KPI، hub tile، form section، empty state،
statement filter و چند سطح کلیدی اعمال می‌کند. بنابراین توصیف قدیمی «کاملاً flat
و بدون shadow» دیگر دقیق نیست؛ ظاهر فعلی shadow بسیار نرم و کم‌کنتراست دارد.

## 8. Layout، Sidebar و Header

### 8.1 ساختار Layout

ساختار authenticated به‌صورت زیر است:

1. Sidebar ثابت/چسبان
2. Main shell
3. Header
4. ناحیه alert/toast
5. PageTopCards
6. SummaryCards
7. SectionTabs
8. Body
9. Footer
10. Shared search dialog، account drawer و iframe modal

صفحه‌های اصلی در `.ptg-page` و `.ptg-page-frame` قرار می‌گیرند. Scroll root
عملاً روی app shell قرار گرفته تا scrollbar در RTL در سمت درست دیده شود.

### 8.2 Sidebar

- عرض expanded: `224px`
- عرض collapsed/mini: `88px`
- عرض token موبایل: `304px`
- سطح تیره یکپارچه `#2A2D45`
- لوگوی سفید `white-sidebar.png` بدون plate روشن
- متن و آیکون سفید پرکنتراست
- آیتم اصلی: radius حدود 8px، متن 15px/500؛ active با وزن 700
- زیرمنو: متن 14px/500، indentation منطقی و نشان active
- Logout رنگ مستقل `#FF9A9A`
- در عرض 992–1199 حالت mini فعال می‌شود.
- در عرض ≤991 Sidebar به off-canvas تبدیل می‌شود؛ در RTL از راست و در LTR از چپ.

گروه‌های ناوبری فعلی:

- Dashboard
- Contracts
- Operations
- Assets
- Finance
- Reports
- Parties: Partners، Companies، Suppliers، Customers، Service Providers،
  Sarrafs، Employees
- Transport: Trucks، Wagons، Drivers، Vessels
- Base Definitions: Products، Units، Currencies، Daily FX، Ports، Expense Types،
  Expense Rules، Storage Tanks، Terminals
- Administration مشروط به permission: Users، Roles، Logs، Backups

### 8.3 Header

- ارتفاع 56px؛ 52px در موبایل
- sticky و بدون border/shadow غالب
- پس‌زمینه در حالت عادی شفاف/هم‌رنگ shell؛ حالت scrolled سفید نیمه‌شفاف
- blur token برابر صفر است؛ effect شیشه‌ای واقعی ایجاد نمی‌شود.
- سمت آغاز: hamburger/collapse
- سمت پایان: search trigger، fiscal-year switcher، flag زبان، account avatar
- دکمه‌های icon حدود 40px و دایره/گرد
- Flag بر اساس زبان: افغانستان برای فارسی/دری و UK برای انگلیسی
- آواتار Header از `user.webp` و به‌صورت دایره‌ای
- Search dialog: عرض `min(92vw, 640px)`، radius 14px، input ارتفاع 52px
- Account drawer: عرض 360px، آواتار 96px با ring سبز و account switch avatar 40px

## 9. الگوهای صفحه

### 9.1 List page

الگوی غالب `.ak-list-page` است:

- Page header شامل title، subtitle اختیاری و CTA
- KPI/stat grid اختیاری
- filter rail
- table تخت
- pagination/action area

61 View از marker مستقیم list-page استفاده می‌کنند. `ak-table` در 234 occurrence و
`ak-table-wrap` در 243 occurrence دیده شد.

### 9.2 Form page

الگوی غالب `.ak-form-page > form.ak-form` است:

- عرض 860px یا wide برابر 1080px
- sectionهای عمودی با فاصله 52px
- grid دو ستونه با gap افقی 32px و عمودی 24px
- field شامل label، control، helper/error
- section title تکراری در بعضی فرم‌ها پنهان می‌شود تا header دوباره نمایش داده نشود.
- fieldهای کم‌استفاده در `<details class="ak-advanced">`
- footer actions با divider بالایی و فاصله 20px
- Save آبی، عرض 84px، ارتفاع حداقل 36px، radius 12px
- Cancel شفاف و text-only
- در موبایل grid یک ستونه می‌شود.

در source، `ak-form-page` حدود 165 بار، `ak-form-section` 485 بار، `ak-field`
1091 بار و `ak-input` 1087 بار استفاده شده است.

### 9.3 Detail page

42 Detail View با تست ساختاری به قرارداد AK Detail v2 متصل‌اند:

- root marker مشخص
- shared page header
- summary/stat cards
- tab rail مشترک
- panelهای اطلاعاتی و tableهای مرتبط

در Detailها اعداد LTR باقی می‌مانند؛ alignment باید روی container RTL تنظیم شود،
نه با تغییر direction خود مقدار.

### 9.4 Hub و Report directory

Hub tile:

- grid با `minmax(260px, 1fr)`
- gap 12px
- padding 16px
- radius 8px
- title 15px/500 و description 12px/1.6

Reports overview استثنای آگاهانه است:

- دو ستون در desktop و یک ستون زیر 992px
- row حداقل 110px با gap 24px
- icon 52px و font icon حدود 40px
- border-bottom و بدون card مجزا
- title 15px، description 13px

## 10. KPI Card و Dashboard

### 10.1 KPI Card

ساختار:

- grid حداکثر چهار ستون
- gap `clamp(18px, 2.2vw, 32px)`
- Card با نسبت `2.6:1`، حداکثر ارتفاع 175px
- تقسیم داخلی desktop: متن 45% و Illustration برابر 55%
- padding `11–16px`
- سطح سفید، radius 18px و shadow بسیار نرم
- بلوک متن از نظر فیزیکی چپ و Illustration راست ثابت است؛ متن داخلی RTL است.
- مقدار عددی LTR و tabular
- trend در کف card قرار می‌گیرد.

Responsive:

- زیر 1400: gap 12px، نسبت 2.1، ارتفاع 78–128px، radius 14px و تقسیم 58/42
- زیر 900: gap 9px، نسبت 1.95، radius 12px و تقسیم 63/37
- زیر 600: دو ستون؛ نه یک ستون

Loading state از skeleton gradient استفاده می‌کند و در
`prefers-reduced-motion: reduce` animation حذف می‌شود.

### 10.2 Dashboard

Dashboard از این ترتیب استفاده می‌کند:

1. چهار KPI Card
2. analytics area شامل donut/mix و trend chart
3. هشت quick-access tile

ویژگی‌ها:

- panelها radius حدود 16px و shadow نرم دارند.
- chartها SVG/JavaScript هستند.
- quick access از Solar iconهای inline استفاده می‌کند.
- رنگ‌های chart به‌طور موضعی در View نیز hard-code شده‌اند.
- breakpointهای اصلی Dashboard: 1200، 768 و 576px
- hero/banner تزئینی وجود ندارد؛ Dashboard داده‌محور و action-oriented است.

## 11. Table

الگوی غالب `.ak-table-wrap > table.ak-table`:

- wrapper دارای `overflow-x: auto`
- table تمام‌عرض و `border-collapse: collapse`
- سطح بیرونی تخت؛ card frame و shadow تزئینی ندارد.
- header band برابر `#F4F6FB`
- header نهایی: 13px/500، padding تقریباً 11px × 14px
- cell نهایی: 15px/400، padding تقریباً 12px × 14px
- divider ردیف: `#E5E7EB`
- hover: `#F5F7FA` یا `#F7F8FA`
- selected row: `#F2F4FC`
- name link: متن اصلی، وزن 500؛ hover بنفش و underline
- ستون‌های عددی با `ak-num`، LTR و tabular
- actionهای ردیف در hover، focus-within یا selected آشکار می‌شوند.
- nested/subrow با سطح soft و متن 13px نمایش داده می‌شود.

دو راهبرد responsive هم‌زمان وجود دارد:

- جدول‌های `ak-table`: حفظ ساختار و horizontal scroll
- جدول‌های legacy با `is-responsive-table`/`ds-table`: تبدیل هر row به card در موبایل

جدول‌های Document/Statement و Import استثناهای تخصصی‌اند. Import Loading تا
`min-width: 1440px`، row حدود 76px و sticky column دارد.

## 12. Form controls

Input/select استاندارد:

- height: 42px
- padding: `0 12px`
- border: `1px solid #CFD1DE`
- radius: 8px
- background: `#FFFFFF`
- font: 14px
- hover border: `#BCBFD2`
- focus border: `#55588B`
- focus ring: 3px با opacity حدود 0.12
- disabled background: `#EEF0F5`
- placeholder: `#9AA0B5`
- error: `#B80000`/`#CC0000`

Textarea ارتفاع وابسته به محتوا دارد. Entity combobox یک select واقعی را برای
binding نگه می‌دارد و UI جست‌وجوپذیر روی آن می‌سازد؛ input آن نیز 42px است.

Toggle:

- host حداقل 42px
- switch حدود `2.1em × 1.15em`
- label 13px/500

Upload:

- تمام‌عرض
- حداقل ارتفاع 80px
- border dashed
- radius 8px

## 13. Filter و Search

فیلتر canonical از partial مشترک `_AkSearchFilter` و درخواست GET استفاده می‌کند؛
state آن در query string باقی می‌ماند.

- filter host حداقل 48px
- input ارتفاع 48px
- filter chip ارتفاع 36px، radius 6px و background `#F2F4FC`
- remove control حدود 22px و دایره‌ای
- search icon button حدود 28px
- clear button حدود 32px
- popover حداقل 240px و حداکثر 420px
- حداکثر ارتفاع popover حدود 320px
- radius 6px، dropdown shadow و z-index حدود 60
- انواع: text، select، boolean، date و date range
- operator builder یا multi-select عمومی وجود ندارد.

Report pageها به‌جای این الگو، toolbar پارامترهای گزارش خود را دارند. Search
موجود در Header یک dialog برای جست‌وجو/فیلتر در سطح shell است و جایگزین filter
صفحه نیست.

## 14. Button

| نوع | ظاهر |
|---|---|
| Primary/Add/Save | `#1877F2`، متن سفید، hover `#0B5ED7` |
| Secondary | سطح `#F1F2F4`، متن `#6C757D`، hover `#E6E8EB` |
| Brand/Bootstrap secondary | خانواده `#55588B` |
| Danger | `#CC0000` یا variant نرم قرمز |
| Cancel | transparent، متن `#424242`؛ hover بنفش |
| Icon-only | معمولاً 40–44px، با aria-label مورد انتظار |

قاعده عمومی:

- font 14px/600
- min-height حدود 36px
- padding `6px 12px`
- radius 12–13px
- shadow: none
- transition فقط color/background/border/transform؛ فایل legacy هنوز یک
  `transition: all` دارد.

## 15. Badge و Status

Badge/status نهایی:

- inline-flex
- min-height 28px
- padding افقی 12px
- font 13px/600
- radius 999px
- بدون border سنگین
- رنگ soft semantic مطابق جدول بخش 4.4

نام کلاس‌ها باید semantic باشد: `is-active`, `is-inactive`, success, warning,
danger, info, viewed و draft.

## 16. Tabs

Tab system فعلی text-only و بدون card/pill سنگین است:

- font 14px
- متن عادی `#424242`
- active `#55588B`
- border rail `#E5E7EB`
- padding افقی 16px
- padding عمودی حدود 10px بالا و 8px پایین
- indicator فعال 2px
- فاصله بالا 20px و پایین 16px
- motion حدود 180ms
- horizontal overflow در عرض کم
- در موبایل padding افقی 12px
- در print مخفی

Bootstrap tabs مبتنی بر `data-bs-toggle="tab"` قرارداد اصلی این سیستم نیست.
کامپوننت محلی `data-ak-tab` tab contentهای از قبل render شده را فوری جابه‌جا
می‌کند. Cardهای آماری متعلق به هر tab باید قبل از rail مشترک و فقط همراه همان
tab ظاهر شوند.

## 17. Modal

Modalهای برنامه چند لایه CSS دارند:

- backdrop opacity بین 0.22 و 0.30
- modal content سفید
- shadow از dialog token
- header حداقل حدود 68px
- header padding حدود `18px 22px`
- footer padding حدود `16px 22px`
- border divider روشن
- radius عمومی token برابر 8px است، ولی selector اختصاصی modal عادی غالباً
  radius 16px را اعمال می‌کند.

Page modal مبتنی بر iframe:

- desktop: `min(1180px, viewport - 36px)`
- ارتفاع: `min(820px, viewport - 36px)`
- compact: عرض 560px و ارتفاع تا 760px
- mobile: viewport منهای 16px و margin برابر 8px

فرم مودال از `_ModalLayout` و scroll anchor مشترک استفاده می‌کند. فرم کامل
داخل صفحه نباید به scroll-box محدود تبدیل شود.

## 18. Avatar، Icon و Illustration

### 18.1 Avatar

- آواتار کاربر Header: تصویر `user.webp`، دایره‌ای
- آواتار drawer: 96px با ring سبز
- آواتار account switch: 40px
- آواتار شخص در table: 28px
- اگر تصویر شخص موجود نباشد، icon سفید شخص روی دایره بنفش تیره حدود `#3C3F72`
- avatarهای row توسط partial مشترک یا enhancement جدول ساخته می‌شوند.

### 18.2 Illustration کارت آماری

سبک غالب:

- WebP
- زمینه شفاف/سفید بسیار نرم
- Illustrationهای سه‌بعدی یا شبه‌سه‌بعدی با palette آبی، سفید و خاکستری
- موضوع‌های نفت، حمل‌ونقل، قرارداد، پول، گزارش و اشخاص حرفه‌ای
- بدون قاب و بدون سایه مستقل درون visual area
- `object-fit: contain`
- alt خالی چون Illustration تزئینی است؛ article خود aria-label دارد.
- lazy loading و async decoding

Registry مرکزی 82 کلید مفهومی/alias را به 59 asset یکتا وصل می‌کند؛ همه مسیرهای
ثبت‌شده در source فعلی فایل موجود دارند. گروه‌های اصلی:

- `ref-blue`: عملیات، فروش، پرداخت و settlement
- `ref-people`: مشتری، شریک، کارمند، supplier و service provider
- `ref-icons`: تجهیزات، گزارش، واحد، محصول، pipeline و مانند آن
- `ref-shipment`: KPIهای پرونده shipment
- `ref-reports`: گروه‌های گزارش
- `ref-reconciliation`: mismatch/audit
- `ref-contracts`: مجموعه تصویری قرارداد

یک فایل صفر بایتی `ref-icons/r5c6.webp` در پوشه asset وجود دارد، اما در Registry
فعلی استفاده نمی‌شود؛ در UI جاری مسیر ثبت‌شده‌ای به آن دیده نشد.

### 18.3 Icon

- Bootstrap Icons در shell و فرم‌های عمومی
- Solar SVG inline در Dashboard quick access
- SVGهای خطی با stroke/currentColor در actionهای اختصاصی
- اندازه رایج icon: 16–20px؛ report directory تا 40px
- icon تزئینی باید `aria-hidden="true"` و icon button باید نام قابل‌دسترسی داشته باشد.

## 19. RTL، LTR و Responsive

### 19.1 RTL/LTR

- زبان از cookie `ptg-ui-lang` تعیین می‌شود.
- فارسی/دری: `dir="rtl"` و Bootstrap RTL
- انگلیسی: `dir="ltr"` و Bootstrap عادی
- `margin-inline`، `padding-inline` و `inset-inline` در لایه جدید غالب‌اند.
- Sidebar موبایل در RTL از راست و در LTR از چپ وارد می‌شود.
- مبلغ، نرخ، وزن و عدد با `bdi`/LTR و tabular digits نمایش داده می‌شوند.
- alignment عنوان/label RTL است، اما direction عدد نباید RTL شود.
- جدول و tab rail در عرض کم horizontal overflow کنترل‌شده دارند.

### 19.2 Breakpointهای مشاهده‌شده

سیستم یک breakpoint واحد ندارد، اما این نقاط پرتکرارند:

- 2400، 1920 و 1600 برای افزایش Page frame
- 1400 برای فشرده‌سازی KPI و بعضی shellها
- 1200 برای mini/sidebar و typography
- 992 برای off-canvas Sidebar و یک‌ستونه شدن بعضی hubها
- 900 برای فشرده‌سازی KPI
- 768 برای mobile layout/table/form
- 640 و 600 برای report/KPI
- 576 و 480 برای phone

### 19.3 قواعد responsive غالب

- shell desktop از 1200px به بالا
- shell mini بین 992 و 1199px
- off-canvas در 991px و کمتر
- form grid در موبایل یک ستون
- KPI در موبایل دو ستون
- tableهای AK افقی scroll می‌شوند.
- actionها در عرض کم wrap یا full-width می‌شوند.
- page gutters از 40 به 16px کاهش می‌یابد.
- `prefers-reduced-motion` در 11 stylesheet رعایت شده است.

## 20. ناهماهنگی‌ها و نقاط پرریسک

این موارد پیشنهاد redesign نیستند؛ اختلاف‌های واقعی source فعلی‌اند.

### 20.1 اختلاف سند قدیمی با runtime

`docs/UI-DESIGN-SYSTEM.md` هنوز Sidebar روشن، عنوان 36px/300، ظاهر کاملاً flat و
حجم CSS قدیمی را توصیف می‌کند. runtime فعلی Sidebar تیره، عنوان 30px/600 و
panel shadow نرم دارد.

### 20.2 چند منبع رنگ و اندازه

`boltz-shell.css` هنوز palette، radiusهای 14–28px، inputهای 48px و shell قدیمی
را در خود دارد. فایل‌های PTG بیشتر آن را override می‌کنند، اما pageهای تخصصی
گاهی همان legacy language را حفظ کرده‌اند.

### 20.3 دو رنگ «اصلی»

هویت navigation و focus بنفش `#55588B` است، در حالی که CTA آبی `#1877F2` است.
commentها یا سندهای قدیمی گاهی primary را سبز یا بنفش معرفی می‌کنند. ظاهر نهایی
فعلی باید نقش‌ها را جدا نگه دارد.

### 20.4 Radius پراکنده

در CSS مقادیر 6، 8، 10، 12، 14، 16، 18، 20، 24، 28، 999 و 50% هم‌زمان وجود
دارد. مهم‌ترین اختلاف، modal 8/16 و token موبایل 20–24 در برابر componentهای
صریح 8–12 است.

### 20.5 Responsive table دوگانه

بعضی tableها scroll افقی دارند و بعضی در موبایل به card تبدیل می‌شوند. کاربر در
ماژول‌های مختلف رفتار متفاوت می‌بیند.

### 20.6 Typography محلی

Reports overview هنوز fallback قدیمی 36px/300 را در variable fallback دارد،
در حالی که typography نهایی 30px/600 است. چند صفحه تخصصی font-sizeهای
`rem` و weightهای 700–900 legacy را حفظ کرده‌اند.

### 20.7 Modal cascade

`08-modals.css` و `45-akaunting.css` چند تعریف radius، border، shadow و backdrop
دارند. computed value به selector صفحه وابسته است، نه فقط token.

### 20.8 Inline و page-specific CSS

دو View style داخلی دارند. چهار View stylesheet اختصاصی دارند. Document/print
pageها عمداً از `ak-table` دور می‌شوند، اما این موضوع باعث می‌شود ظاهر آن‌ها
کاملاً از table عمومی مشتق نشود.

### 20.9 Color hard-coding

Dashboard، chartها، trendها و چند workspace تخصصی HEXهای مستقیم دارند. تکرار
فراوان `#FFFFFF`، `#424242` و `#1877F2` نشان می‌دهد همه رنگ‌ها از token مصرف
نمی‌شوند.

### 20.10 Status semantic mismatch

در partial مشترک Status، fallback متن «فعال» با کلاس visual غیرفعال همراه می‌شود.
این fallback در صورت نبود مقدار صریح می‌تواند معنی متن و رنگ را متناقض کند.

### 20.11 Dark theme نیمه‌فعال

توکن و selectorهای `data-theme="dark"` وجود دارند، اما shell فعلی light-only
است و theme toggle فعال عمومی ندارد. Dark tokens بخشی از Design System قابل
استفاده فعلی محسوب نمی‌شوند.

### 20.12 Motion و focus

بیشتر transitionها property-specific هستند، اما `boltz-shell.css` یک
`transition: all` گسترده دارد. چند selector `outline: none/0` دارند؛ در بسیاری
از آن‌ها focus ring جایگزین تعریف شده، ولی صحت تمام focus stateها بدون مرورگر
authenticated تأیید نشده است.

### 20.13 آواتار و asset

Illustrationها از چند reference set با سبک نزدیک ولی نه کاملاً یکسان آمده‌اند.
فایل صفر بایتیِ بدون مصرف نیز نشان می‌دهد asset folder نیازمند کنترل registry
محور است؛ صرف وجود فایل نباید به‌معنای قابل‌استفاده بودن آن تلقی شود.

## 21. قواعد لازم برای صفحات جدید

این قواعد از Design System فعلی استخراج شده‌اند و باید برای حفظ هماهنگی رعایت شوند:

1. از `_Layout` و Page frame موجود استفاده شود؛ gutter یا container مستقل ساخته نشود.
2. root صفحه یکی از archetypeهای `ak-list-page`، `ak-form-page` یا
   `ak-detail-page` باشد.
3. رنگ‌های نقش‌دار از tokenها مصرف شوند؛ CTA آبی و brand/navigation بنفش باقی بمانند.
4. فارسی/دری با Vazirmatn و RTL؛ انگلیسی با Poppins و LTR.
5. اعداد، پول، وزن و نرخ با `ak-num`/`bdi`، LTR و tabular digits باشند.
6. عنوان صفحه 30px/600، body 15px و label 14px باشد.
7. عرض معمول صفحه 1200px؛ فرم 860px و فرم دارای line items حداکثر 1080px.
8. grid فرم دو ستون با gap 32×24 و در موبایل یک ستون باشد.
9. input/select استاندارد 42px، radius 8px و border `#CFD1DE` باشد.
10. action اصلی از `ak-primary-action` یا `ak-save` استفاده کند؛ variant تازه
    با رنگ متفاوت ساخته نشود.
11. table از `ak-table-wrap` و `ak-table` استفاده کند؛ ستون عددی `ak-col-num`
    و مقدار `ak-num` داشته باشد.
12. filter صفحه از partial مشترک GET استفاده کند و state در URL بماند.
13. status از semantic class و palette soft موجود استفاده کند؛ متن و tone
    هم‌معنی باشند.
14. KPI از StatCard component و AvatarKey ثبت‌شده استفاده کند؛ path تصویر در
    View hard-code نشود.
15. Illustration صرفاً تزئینی، WebP و `object-fit: contain` باشد.
16. tabها از `data-ak-tab` استفاده کنند؛ panelهای از قبل render شده بدون loader
    و delay جابه‌جا شوند.
17. cardهای متعلق به tab فقط با همان tab دیده شوند و پیش از rail مشترک قرار گیرند.
18. icon-only button نام قابل‌دسترسی و focus state داشته باشد.
19. برای RTL از logical properties استفاده شود؛ left/right فقط در موارد
    فیزیکی آگاهانه مانند KPI layout.
20. Desktop، mini shell، mobile off-canvas، form collapse، table overflow و
    KPI دو ستونه در breakpointهای موجود بررسی شوند.
21. page-specific CSS فقط برای سند/چاپ یا layout واقعاً تخصصی اضافه شود.
22. Dark mode، gradient تزئینی، font جدید، radius جدید یا shadow سنگین به‌عنوان
    بخشی از سیستم فعلی فرض نشود.
23. همه POST formها AntiForgeryToken صریح داشته باشند.
24. فیلد backend برای ساده‌سازی UI حذف یا مخفی نشود؛ field کم‌استفاده به Advanced برود.

## 22. Source map ممیزی

منابع مرجع اصلی:

- `Views/Shared/_Layout.cshtml`
- `Views/Home/Index.cshtml`
- `Views/Shared/Components/StatCard/Default.cshtml`
- `Models/StatCards/StatCardAvatarRegistry.cs`
- `wwwroot/css/ptg/01-tokens.css`
- `wwwroot/css/ptg/03-layout.css`
- `wwwroot/css/ptg/04-sidebar.css`
- `wwwroot/css/ptg/05-components.css`
- `wwwroot/css/ptg/08-modals.css`
- `wwwroot/css/ptg/10-responsive.css`
- `wwwroot/css/ptg/12-dashboard.css`
- `wwwroot/css/ptg/15-system-lists.css`
- `wwwroot/css/ptg/16-system-tabs.css`
- `wwwroot/css/ptg/17-system-forms.css`
- `wwwroot/css/ptg/45-akaunting.css`
- `wwwroot/css/ptg/50-ak-components.css`
- `wwwroot/css/ptg/52-stat-card.css`
- `wwwroot/css/ptg/70-page-frame.css`
- `wwwroot/css/ptg/71-typography.css`
- `wwwroot/css/ptg/72-surfaces.css`
- `wwwroot/css/boltz-shell.css`

## 23. نتیجه

Design System واقعی کنونی را می‌توان چنین خلاصه کرد:

> یک رابط ERP روشن و RTL-first با Sidebar تیره، سطح‌های سفید روی پس‌زمینه
> `#FCFCFC`، typography متراکم اما خوانا با Vazirmatn، برند بنفش، CTA آبی،
> فرم‌های 42px، جدول‌های تخت، status pillهای نرم، KPI Cardهای تصویری و
> responsive shell سه‌حالته.

بزرگ‌ترین خطر برای هماهنگی آینده نبود کامپوننت نیست؛ هم‌زیستی لایه legacy و
لایه `ak-*` و تکرار tokenهاست. برای صفحه جدید باید قراردادهای بخش 21 و
کامپوننت‌های Shared موجود، نه fallbackهای `boltz-shell.css` یا سند قدیمی،
منبع رفتار باشند.
