# PTG UI Design System — visual port

## وضعیت و دامنه

این سند قرارداد ظاهری PTG است. گزارش «دیزاین سیستم مدیریت نواتیک» مرجع بصریِ پورت است، نه کد قابل انتقال: PTG همچنان ASP.NET Core MVC، Razor، Bootstrap 5 RTL و اجزای مشترک `ak-*` را به‌کار می‌برد. نام محصول، لوگو، محتوای نفت و گاز، route، مجوز، مدل، binding، validation، POST، منطق موجودی و Ledger همگی متعلق به PTG می‌مانند.

**وضعیت مهاجرت:** فازهای ۱ تا ۶ برای پوسته، داشبورد/جزئیات، فرم/جدول، مودال/ناوبری، گزارش/نمودار و پاکسازی نهایی در کد اعمال شده‌اند. `78-mashal-reference.css` همچنان لایهٔ runtime نهایی است؛ wrapperها و CSS سازگاری با مصرف‌کنندهٔ زنده حفظ شده‌اند و حذف نشده‌اند. بررسی ایستای UI و syntax اسکریپت نمودار موفق است؛ مرور تصویری واقعی در viewportهای ۳۷۵، ۷۶۸، ۱۰۲۴ و ۱۴۴۰ و تأیید نهایی بصری هنوز لازم است. پسوند `mashal-*` hook runtime است، نه مجوز ساخت visual system موازی.

## اصولی که در همهٔ صفحات ثابت می‌مانند

- **یک زبان بصری:** teal فقط برای پوستهٔ ناوبری، آبی عمیق برای action/link/tab/focus، canvas خاکستری روشن، سطح سفید، کارت‌های سفید با سایهٔ نرم، مرزهای خنثی و جزئیات تزئینی محدود.
- **ابعاد هدف:** header حدود ۴۸px؛ sidebar باز ۲۴۰px و ریل جمع‌شده ۶۴px؛ کنترل استاندارد ۴۰px، کنترل/اکشن برجسته ۴۴px، Save فرم ۴۸px؛ متن بدنه و کنترل ۱۴px؛ عنوان صفحه ۲۴px؛ عنوان بخش ۱۸px.
- **شکل و عمق:** کنترل و دکمه ۸px، کارت ۱۶px، پنل‌های متمایز shadow نرم؛ hover-lift فقط برای عنصر واقعاً کلیک‌پذیر. CTA پررنگ ساده و بدون گرادیان سراسری است؛ برای وضعیت/نمودار رنگ معنایی از accent برند جداست.
- **رنگ هدف:** sidebar `#007DB2` و active آن `#006088`؛ CTA/link/tab/focus آبی `#2563EB` با hover `#1D4ED8`؛ canvas `#F3F4F8`؛ سطح `#FFFFFF`؛ neutral `#F8FAFC`؛ متن `#334155`؛ متن فرعی `#64748B`؛ divider `#E2E8F0`؛ موفق `#047857`؛ هشدار `#B45309`؛ خطا `#B91C1C`. آبی روشن مرجع `#3B82F6` در CTA با متن سفید نسبت ۴٫۵:۱ را نمی‌گذراند؛ رنگ action عمداً به نزدیک‌ترین shade قابل‌دسترسی تیره شده است. رنگ به‌تنهایی معنا را منتقل نکند.
- فونت محتوای فارسی/دری IRANSans با fallback Vazirmatn؛ عدد/پول/rate در `dir=ltr` یا `<bdi>` و tabular figures؛ زبان و لحن PTG حفظ شود.
- RTL با Bootstrap RTL و logical properties؛ mobile ابتدا محتوا را نگه می‌دارد و گروه‌بندی می‌کند، دادهٔ backend را حذف نمی‌کند. در viewportهای ۳۷۵، ۷۶۸، ۱۰۲۴ و ۱۴۴۰ پیکسل کل صفحه overflow افقی ندارد؛ جدول پهن فقط در wrapper خودش scroll می‌شود.
- فوکوس صفحه‌کلید آشکار، label متصل به کنترل، اندازهٔ هدف لمسی مناسب، reduced-motion، `aria-live` برای پیام پویا، و semantics صحیح button/link لازم‌اند.

## قرارداد کامپوننت‌ها

### پوسته، Header و Sidebar

Header سفید و فشرده: جای لوگوی اصلی PTG، تاریخ/دوره، کلیدهای عملیاتی و منوی حساب در جای خود باقی می‌مانند؛ عرض/موقعیت‌ها در موبایل بازچینی می‌شوند. Sidebar از teal برند با متن و آیکن پرکنتراست، active state تیره‌تر، جستجو و حالت جمع‌شده استفاده می‌کند. آیتم‌های منو، مجوزها، ترتیب و route تغییر نمی‌کنند. tooltip فقط در حالت rail و با keyboard/focus نیز قابل‌دسترسی است.

### Header صفحه، Toolbar و اقدام‌ها

`_AkPageHeader` مالک H1، context و گروه اقدام است. یک CTA اصلی؛ Export، edit و اقدام کم‌تکرار ثانویه؛ destructive در منو با تأیید. عنوان صفحه در mobile حذف نمی‌شود. `_AkSearchFilter` تنها مالک جست‌وجو/فیلتر صفحه است؛ query string، scope، sorting و pagination موجود دست‌نخورده می‌مانند.

### Panel و StatCard

از سطح سفید با padding متوازن و radius حدود ۱۶px استفاده شود. Static panel حرکت hover ندارد. StatCard فقط عدد تصمیم‌ساز واقعی نشان می‌دهد؛ mobile در grid دوستونهٔ فشرده، desktop تا چهار ستون. مقدار طولانی truncate یا wrap کنترل‌شده دارد، واحد کنار عدد و ارقام tabular هستند. از آیکن/تصویر تزئینی و KPI تکراری پرهیز شود.

### Tabs

فقط یک مدل انتخاب شود: navigation tabها لینک واقعی و URL را تغییر دهند؛ tab تعاملی باید panel، `aria-controls`، انتخاب/فوکوس و keyboard behavior کامل داشته باشد. labelها کوتاه و rail در موبایل قابل پیمایش است؛ تب جدید فقط برای حوزهٔ اطلاعاتی مستقل.

### Form و Field

`.ak-form`, `.ak-form-section`, `.ak-form-grid`, `.ak-field`, `.ak-label`, `.ak-input` و `_AkFooterActions` مرجع‌اند. فرم معمولی دو ستون desktop و تک‌ستون موبایل؛ سطرهای ویرایشگر فقط به‌اندازهٔ نیاز عملیاتی چندستونه می‌شوند. همهٔ فیلدهای موجود و name/id/data-hook حفظ شوند؛ فیلد کم‌استفاده فقط طبق قرارداد به Advanced منتقل شود، نه حذف/پنهان. خطای نزدیک فیلد و خلاصهٔ خطا در ابتدای فرم؛ حالت loading/resubmit روشن. همهٔ POSTها `@Html.AntiForgeryToken()` صریح دارند.

### Table و Data

از `.ak-table-wrap`، `.ak-table`، `.ak-num`، `.ak-status`، `_PersonCell`، `_Capsule` و `.ak-row-menu` استفاده شود. header و cell در desktop حداقل خوانایی ۱۴px؛ هویت، metadata، عدد، وضعیت و action ترتیب معنایی دارند. هر جدول موبایل باید برای همهٔ داده‌ها راه دسترسی داشته باشد: کارت‌سازی فقط با label صریح برای هر مقدار، وگرنه wrapper افقیِ محلی. کلیک روی ردیف جایگزین لینک/دکمهٔ معنایی نیست. جمع و currency/units حذف نمی‌شوند.

### Badge، Money و Progress

`_StatusBadge`/`.ak-status` وضعیت معنایی را با متن و رنگ نشان می‌دهند؛ مجموعهٔ tone محدود به success/info/warning/danger/neutral است. «معلق» یا «متوقف» در ماژول‌های مختلف باید بر اساس معنا نگاشت شود، نه رنگ دلخواه. ارقام با tabular figures، currency/units مشخص و LTR isolation؛ مبالغ مخفی/نمایش‌داده‌شده باید در تمام سطوح (از جمله modal) سیاست امنیتی واحد داشته باشند. Progressbar دارای label و `aria-valuenow/min/max` است.

### Modal، Toast و feedback

Bootstrap modal و lifecycle موجود را حفظ کنید: focus trap، Escape، بازگرداندن فوکوس، body scroll و stacking به bootstrap/manager موجود واگذار شود؛ nested modal تازه ساخته نشود. Toast برای success/info زمان‌دار، برای خطای مهم ماندگار یا قابل‌بستن؛ پیام `aria-live` و قابل‌اقدام باشد. Empty state علت‌محور، کوتاه و در صورت امکان دارای action واقعی است.

### Dashboard و Charts

Dashboard همچنان داده‌های واقعی عملیاتی PTG را نشان می‌دهد؛ هدف بصری گزارش—چهار KPI، دو ناحیهٔ تحلیلی و خلاصه‌های قابل اسکن—به محدودیت داده و تصمیم‌های نفت/گاز تطبیق می‌یابد، نه این‌که ماژول‌های مشتری/پروژهٔ محصول مرجع وارد PTG شوند. رنگ نمودار با متن/legend تفکیک شود و جدول یا خلاصهٔ متنی جایگزینِ دسترس‌پذیر داشته باشد.

### Print، PDF و Excel

قالب invoice، اسناد حقوقی، PDF و Excel جزو design system هستند اما visual shell آن‌ها با صفحات authenticated یکی نیست. خروجی‌های رسمی، ترتیب/فونت/واحد/RTL/صفحه‌بندی و business content خود را حفظ می‌کنند؛ print stylesheet نباید header/sidebar برنامه را روی سند چاپ کند.

## مهاجرت و پاک‌سازی

1. فازهای ۱ تا ۶ (shell/tokens، dashboard/details، form/table، modal/navigation، reports/charts و cleanup) در کد اعمال شده‌اند؛ پیش از sign-off، viewportهای هدف و صفحات/خروجی‌های حساس باید در مرورگر واقعی بازبینی شوند.
2. برای هر خانواده: دو صفحهٔ مرجع، markup، JS hooks، route/model/validation و viewport بررسی شود؛ حداقل تغییر shared انجام گیرد.
3. CSS/JS قدیمی با جست‌وجوی کل `src/PTGOilSystem.Web` و references در Razor/JS/TagHelper/bundle سنجیده شود. حذف فقط وقتی مجاز است که selector/script هیچ مصرف‌کنندهٔ runtime نداشته باشد و targeted UI/build checks بگذرند.
4. هر POST، permission، field، route، workflow مالی و stock invariant حفظ شود. اصلاح business logic، schema یا محاسبه خارج از این مهاجرت است.
5. stylesheetهای compatibility و wrapperهای route تا زمانی که مصرف‌کنندهٔ runtime دارند حذف نمی‌شوند؛ style جدید در آن‌ها نوشته نمی‌شود. فایل مرجع نهایی `78-mashal-reference.css` است و componentها در مالک shared خود باقی می‌مانند.

## قرارداد فنی ثابت PTG

- ASP.NET Core MVC/Razor + Bootstrap 5 RTL؛ بدون مهاجرت به React/Tailwind/shadcn.
- `Views/Shared/Components/Ak/` و `Views/Shared/Partials/` بر markup موازی اولویت دارند.
- مسیر `.NET`، data binding، validation، permission، antiforgery، SPA lifecycle، حسابداری/Ledger و موجودی در این کار تغییر نمی‌کنند.
- CSS جدید فقط اگر component/token موجود پاسخ ندهد؛ selector جدید باید دقیق و در لایهٔ مالک component باشد، نه skin موازی صفحه.
- CSS صفحه‌ای در ناوبری SPA باید با page asset قراردادشده بیاید؛ `@section Styles` تنها به‌تنهایی کافی نیست.
