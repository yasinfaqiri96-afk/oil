# PTG Oil System — UI/UX Design System

> **Source of Truth:** این فایل منبع اصلی و دائمی طراحی پروژه است.
> پیش از طراحی یا اصلاح هر صفحه باید این فایل خوانده شود.
> سپس راهنمای همان نوع صفحه در `design-system/pages/` خوانده شود.
> `ui-ux-pro-max` فقط در چارچوب این سند استفاده می‌شود؛ پیشنهاد عمومی Skill حق تغییر هویت PTG را ندارد.
> هماهنگی کل نرم‌افزار مهم‌تر از نوآوری جداگانه در یک صفحه است.

## 1. دامنه و ترتیب اولویت

این Design System برای **PTG Oil System — Petroleum Trade & Operations ERP** است: یک ERP عملیاتی و مالی برای قرارداد، خرید، بارگیری، حمل، موجودی، فروش، پرداخت، حسابداری و گزارش.

هر تصمیم UI/UX باید به این ترتیب حل شود:

1. رفتار تجاری، صحت مالی/موجودی و سرعت انجام کار
2. قراردادهای معتبر موجود Razor، Bootstrap RTL و `ak-*`
3. توکن‌های runtime پروژه در `01-tokens.css`
4. قواعد این سند و override نوع صفحه
5. پیشنهادهای عمومی `ui-ux-pro-max`

هیچ تصمیم بصری نباید Controller، Model، Route، binding، validation، permission، POST، modal، dropdown، SPA lifecycle، محاسبات، Ledger یا Inventory را تغییر دهد.

## 2. منابع runtime معتبر

منابع زیر به‌ترتیب مرجع اجرای واقعی‌اند:

- توکن‌ها: `src/PTGOilSystem.Web/wwwroot/css/ptg/01-tokens.css`
- پوسته و ترتیب assets: `src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml`
- اجزای ساختاری `ak-*`: `src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css`
- قواعد عمومی AK: `src/PTGOilSystem.Web/wwwroot/css/ptg/45-akaunting.css`
- قاب صفحه و تایپوگرافی: `70-page-frame.css` و `71-typography.css`
- سطح پنل‌ها: `72-surfaces.css`
- خانواده Details: `73-detail-system.css`
- مستند موجود AK: `docs/UI-DESIGN-SYSTEM.md`

اگر مقدار این سند با runtime اختلاف داشت، ابتدا runtime را دوباره بررسی کن؛ بدون درخواست صریح CSS برنامه را برای هماهنگ‌کردن سند تغییر نده.

## 3. شخصیت بصری

- روشن، حرفه‌ای، آرام و Enterprise
- لوکسِ کنترل‌شده؛ نه نمایشی
- تراکم متوسط و مناسب ERP
- سلسله‌مراتب واضح برای مقدار، پول، وضعیت و اقدام
- کارت و رنگ فقط وقتی معنای اطلاعاتی دارند
- Bootstrap 5 RTL، Vazirmatn و Bootstrap Icons
- بدون Tailwind، React، Vue، shadcn یا Design System موازی

ظاهر PTG نباید شبیه landing page، محصول کودکانه، داشبورد نمایشی یا قالب عمومی AI شود.

## 4. Design Tokens

### 4.1 رنگ‌ها

| نقش | توکن runtime | مقدار فعلی | کاربرد |
|---|---|---:|---|
| Navigation / interaction | `--primary-main` | `#173F73` | لینک، تب، focus، shell |
| Navigation hover | `--primary-dark` | `#123258` | hover/focus تعاملی |
| Primary action | `--ptg-btn-primary` | `#1877F2` | Save/Create/عمل اصلی |
| Primary action hover | `--ptg-btn-primary-dark` | `#0B5ED7` | hover/active |
| Canvas | `--background-default` | `#FCFCFC` | پس‌زمینه اصلی |
| Paper | `--background-paper` | `#FFFFFF` | modal، dropdown، سطح سفید |
| Neutral surface | `--background-neutral` | `#F5F7FA` | hover و ردیف نرم |
| Text | `--text-primary` | `#424242` | عنوان، متن، عدد |
| Muted text | `--text-secondary` | `#666B75` | توضیح و metadata |
| Divider | `--divider` | `#E5E7EB` | جداکننده و جدول |
| Success | `--success-main` | `#6EA152` | فقط موفقیت واقعی |
| Warning | `--warning-main` | `#F59E0B` | نیازمند توجه |
| Error | `--error-main` | `#CC0000` | خطا/عمل مخرب |
| Info | `--info-main` | `#006EA6` | اطلاع خنثی |

`#206BC4` و `#00A76F` در حال حاضر توکن canonical runtime نیستند. آن‌ها را مستقیم یا صفحه‌ای وارد نکن؛ هر تغییر پالت باید ابتدا در `01-tokens.css` و با درخواست مستقل انجام شود.

قواعد رنگ:

- رنگ وضعیت فقط برای state واقعی و همراه با متن/آیکون استفاده شود.
- پول مثبت/منفی را فقط در جای تصمیم‌ساز رنگی کن؛ متن مالی عادی تیره بماند.
- CTA اصلی آبی است؛ success جای CTA را نمی‌گیرد.
- در هر ناحیه معمولاً یک accent اصلی کافی است.
- رنگ hard-coded جدید ممنوع؛ از توکن موجود استفاده کن.

### 4.2 تایپوگرافی

خانواده اصلی `Vazirmatn` است. Poppins فقط بخشی از shell legacy است و نباید برای محتوای جدید انتخاب شود.

| نقش | اندازه / وزن runtime |
|---|---|
| عنوان صفحه | `30px / 600` |
| عنوان سکشن | `19px / 600` |
| عنوان کارت | `17px / 600` |
| متن بدنه | `15px / 400`, line-height `1.65` |
| Label | `14px / 600` |
| هدر جدول | `13px / 500` |
| سلول جدول | `15px / 400` |
| عدد پرتکرار | `14.5px / 500` |
| KPI | `26px / 600` |
| Caption / Status | `13px / 500–600` |
| Button | `14px / 600` |

قواعد:

- برای hierarchy از scale موجود استفاده کن، نه وزن‌های 800/900 و عنوان‌های بسیار بزرگ.
- عدد، ارز، نرخ و مقدار با `font-variant-numeric: tabular-nums` نمایش داده شوند.
- بلوک عدد/واحد می‌تواند `dir="ltr"` یا `<bdi>` داشته باشد؛ ترتیب کلی صفحه RTL می‌ماند.
- متن جدول کوتاه، دقیق و قابل اسکن باشد.

### 4.3 فاصله و چگالی

Scale اصلی 4px است. توکن‌های رایج:

- فاصله ریز: `4px`, `8px`
- gap معمول: `--ptg-space-gap: 12px`
- padding کارت/صفحه: `--ptg-space-card: 16px`
- gap بزرگ: `--ptg-space-gap-lg: 24px`
- gap فیلد: `--ptg-space-field: 12px`
- فاصله سکشن فرم: `--ak-form-section-gap: 48px`

ERP باید متراکم اما تنفس‌پذیر باشد:

- داده‌های مرتبط نزدیک هم؛ سکشن‌های مستقل با فاصله روشن
- whitespace تزئینی و hero spacing ممنوع
- در desktop اطلاعات تصمیم‌ساز بالای fold بماند
- در mobile stacking بر حذف داده اولویت دارد

### 4.4 Radius، Border و Shadow

- Input / card canonical: `8px`
- کوچک: `6px`
- بزرگ کنترل‌شده: `12px`
- badge: `12px`
- avatar: دایره کامل
- Border: `1px` و فقط برای ساختار/کنترل
- پنل‌های محتوایی از `--ptg-panel-shadow` استفاده می‌کنند
- dropdown/modal از `--shadow-dropdown` و `--shadow-dialog`

Border ضخیم، radius بزرگ، glow، shadow سنگین و elevation تودرتو ممنوع است.

### 4.5 Layout

| نقش | مقدار |
|---|---:|
| Sidebar expanded | `224px` |
| Sidebar rail | `88px` |
| Mobile navigation | `304px` |
| Header desktop/mobile | `56px / 52px` |
| Content max | `1200px` |
| Form max | `860px` |
| Wide form max | `1080px` |
| Gutter desktop/laptop/compact/mobile | `40 / 32 / 24 / 16px` |

Breakpointهای canonical Bootstrap:

- `<576px`: mobile کوچک
- `<768px`: mobile/tablet کوچک
- `<992px`: shell drawer و تک‌ستون اصلی
- `<1200px`: laptop
- `≥1200px`: desktop expanded

صفحه باید در 375، 768، 1024 و 1440 پیکسل بررسی شود. جدول‌های واقعی می‌توانند در wrapper افقی scroll شوند؛ کل صفحه نباید horizontal scroll بگیرد.

### 4.6 Motion

- سریع: `140ms`
- عادی: `190ms`
- ورود کوتاه: حدود `200ms` با حداکثر `6px`
- فقط `opacity` و `transform`
- ردیف‌های جدول animate نمی‌شوند
- form control، dropdown و datepicker حرکت نمایشی ندارند
- `prefers-reduced-motion` الزامی است

## 5. Shell و جهت

- `_Layout.cshtml` مالک Sidebar، Topbar، جستجو، زبان، سال مالی و lifecycle assets است.
- Sidebar تیره و محتوای اصلی روشن است؛ رنگ shell را صفحه‌ای override نکن.
- در فارسی/دری `dir="rtl"` و در UI انگلیسی `ltr` حفظ شود.
- CSS جدید باید از logical properties مثل `margin-inline` و `inset-inline` استفاده کند.
- `spa-nav.js` shell را نگه می‌دارد و `<main>` را جایگزین می‌کند؛ initialization باید idempotent و سازگار با `ptg:page-ready` باشد.

## 6. کامپوننت‌های canonical و قابل Reuse

### Header و actions

- `_AkPageHeader`: عنوان، status، context، اکشن اصلی، برگشت و kebab
- `_AkSectionHead`: عنوان و توضیح سکشن
- `_AkFooterActions`: Save و Cancel فرم
- `_DetailActionBar`: next actions در Details

قواعد:

- یک primary action واضح در header یا footer
- secondary action خنثی/outline
- edit، print، export و destructive ثانویه در kebab یا toolbar
- عمل مخرب همیشه متن روشن، تأیید و anti-forgery داشته باشد

### KPI

- `StatCard/Default.cshtml` در `.ak-stat-grid`
- معمولاً 3 تا 5 KPI، فقط از داده واقعی
- KPI باید پاسخ‌گوی تصمیم فوری باشد؛ آمار تزئینی ممنوع
- کارت‌های بسیار زیاد را در tab مربوط نگه دار

### List و table

- `.ak-list-page`, `.ak-list`, `.ak-table-wrap`, `.ak-table`
- `_AkSearchFilter` تنها Search/Filter canonical
- `_ExportMenu`, `_PagedListFooter` / `.ak-pager`
- `_PersonCell`, `_Capsule`, `_StatusBadge` و `.ak-row-menu`

ستون identity در ابتدا، metadata بعد، اعداد و پول هم‌تراز، status نزدیک انتهای ردیف و actions در ستون آخر.

### Form

- `_CreatePageShell` / `_EditPageShell` در CRUD استاندارد
- `.ak-form`, `.ak-form-section`, `.ak-form-grid`
- `.ak-field`, `.ak-label`, `.ak-input`, `.ak-field-error`
- `AkEntityComboboxTagHelper` برای انتخاب موجودیت و Quick Create
- validation نزدیک فیلد و summary در ابتدای فرم
- همه POSTها دارای `@Html.AntiForgeryToken()` صریح
- فیلد backend حذف یا مخفی نشود؛ فیلد کم‌استفاده به Advanced منتقل شود

### Details

- `.ak-detail-page` + `data-ak-detail-v2="true"`
- `_AkPageHeader`, `_DetailKpiStrip`, `_DetailSummaryCard`
- `_DetailsTabs`, `_DetailTimeline`, `_RelatedRecords`
- `_OperationsDetailMore`, `_DetailPager`, `_DetailEmptyState`

ساختار مطلوب:

1. هویت و وضعیت
2. 3–5 KPI تصمیم‌ساز
3. خلاصه/صورت‌حساب
4. تب‌ها یا بخش‌های جزئیات
5. سابقه، روابط و اطلاعات فنی در More/Advanced

### Tabs

- فقط `ptg-tabs-rail` و `ptg-tab-item`
- 3 تا 7 تب برای رکورد پیچیده؛ در صفحه ساده 3 تا 5
- label کوتاه، active state ثابت، tab rail قابل scroll در موبایل
- tab جدید فقط برای حوزه اطلاعاتی مستقل، نه برای زیبایی

### Modal

- Bootstrap modal با `.ak-modal-content` یا `.ak-page-modal`
- عنوان، close، body و footer روشن
- compact برای فرم کوتاه؛ modal بزرگ فقط برای workflow ضروری
- nested modal، blur نمایشی و full-screen بی‌دلیل ممنوع
- submit، validation و close/redirect موجود حفظ شود

### Feedback

- `_FlashAlerts`, `_ToastNotifications`, `.ak-empty`, `_DetailEmptyState`
- loading واقعی با loader/skeleton موجود؛ layout shift ایجاد نشود
- Empty state کوتاه، علت‌محور و در صورت امکان دارای action واقعی
- خطا کنار منبع و به زبان قابل اقدام

## 7. اطلاعات مالی و عملیاتی

- پول، ارز، نرخ و مقدار باید واحد واضح داشته باشند.
- `Credit/Debit`، مانده و علامت عدد طبق semantics همان ماژول باقی بماند.
- مجموع‌ها از ردیف جزئی قوی‌تر ولی از عنوان صفحه ضعیف‌تر باشند.
- status رنگی جای عدد، label یا توضیح را نگیرد.
- اطلاعات تصمیم‌ساز: مبلغ، مقدار، مانده، وضعیت، طرف، تاریخ و منبع.
- شناسه داخلی، trace و metadata فنی در Advanced/More قرار گیرد؛ حذف نشود.
- در عملیات ابتدا «چه چیزی، کجا، چه مقدار، در چه وضعیت»؛ در مالی ابتدا «طرف، مبلغ، ارز، جهت، مانده، سند».

## 8. تراکم مناسب بر حسب نوع صفحه

- Dashboard: 4 KPI اصلی + حداکثر 2 visualization + یک خلاصه عملیاتی
- List: header، 3–4 KPI اختیاری، یک toolbar، یک table/list، pager
- Create/Edit: 3–5 سکشن معنادار، grid دو ستونه desktop و تک‌ستون mobile
- Details: 3–5 KPI و 3–5 بخش اصلی؛ جزئیات فنی collapsed
- Reports: پارامترها فشرده، summary بالا، جدول/چارت اصلی، export نزدیک پارامترها
- Financial: چگالی بالاتر، رنگ کمتر، عدد و reconciliation برجسته‌تر
- Operational: status و lineage قوی‌تر، action نزدیک مرحله جاری

## 9. CSS و JavaScript

- برای صفحه جدید CSS مستقل، skin، variant، token یا `!important` نساز.
- ابتدا component/token موجود را پیدا کن.
- اگر قاعده جدید واقعاً shared است، در لایه shared مناسب و با scope دقیق اضافه شود.
- `site.css`, `boltz-shell.css`, `13-compat.css` و `17-system-forms.css` منبع الگوی جدید نیستند؛ فقط legacy/bridge هستند.
- inline style ممنوع.
- data attributeهای موجود قرارداد JS هستند و نباید برای ظاهر rename شوند.
- initialization روی `DOMContentLoaded` و `ptg:page-ready` تکرارپذیر باشد.
- از ساخت framework یا state store جدید خودداری کن.

## 10. Anti-patternهای قطعی

- طراحی بی‌ربط با shell و صفحات هم‌خانواده
- تغییر ناگهانی پالت یا فونت
- Dark OLED به‌عنوان default
- Inter به‌جای Vazirmatn
- Tailwind/shadcn/React/Vue
- Single-column landing pattern برای ERP
- dashboard کردن هر صفحه
- KPI، داده، نمودار یا empty state جعلی
- card nesting و کارت‌سازی بی‌دلیل
- gradient، neon، glow و glassmorphism شدید
- shadow سنگین، border زیاد و radius بسیار بزرگ
- icon یا avatar تزئینی
- حذف داده واقعی برای خلوت‌شدن
- مخفی‌کردن primary action
- رنگ وضعیت بدون semantics
- فرم طولانی بدون گروه‌بندی
- table ناخوانا یا تبدیل همه داده‌ها به card در mobile
- JS/CSS موازی برای component موجود
- تغییر business logic برای آسان‌شدن طراحی

## 11. روند اجباری هر درخواست UI/UX

1. `git status` و فایل‌های dirty مرتبط را بررسی و حفظ کن.
2. Skill محلی `.claude/skills/ui-ux-pro-max/SKILL.md` را بخوان.
3. این `MASTER.md` را بخوان.
4. guideline نوع صفحه را بخوان.
5. دو صفحه هم‌خانواده و componentهای مشترک را بررسی کن.
6. business behavior، route، binding، validation و JS hooks را فهرست کن.
7. فایل‌ها و plan کوتاه را پیش از edit اعلام کن.
8. کوچک‌ترین تغییر shared و سازگار را اعمال کن.
9. viewport، RTL، keyboard/focus، loading/empty/error و اعداد را بررسی کن.
10. Web build و targeted test مرتبط را اجرا کن.
11. فایل‌های تغییرکرده، تست و محدودیت browser را صادقانه گزارش کن.

## 12. معیار پذیرش

- با پوسته و صفحات هم‌خانواده یکی دیده می‌شود.
- hierarchy اطلاعات از business workflow پیروی می‌کند.
- داده، رفتار، permission و حسابداری تغییر نکرده است.
- component canonical reuse شده است.
- primary/secondary/destructive action hierarchy روشن است.
- RTL، responsive، focus و contrast رعایت شده است.
- هیچ token، framework، CSS یا JS موازی ساخته نشده است.
- build/test متناسب با ریسک اجرا شده است.

