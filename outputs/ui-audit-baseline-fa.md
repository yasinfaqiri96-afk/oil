# ممیزی طراحی رابط PTG Oil System — خط مبنا و یافته‌های مرحلهٔ اول

**تاریخ:** ۲۰۲۶-۱۰-۲۳
**دامنهٔ این مرحله:** بررسی read-only مستندات Design System، Razor، CSS و پوستهٔ مشترک؛ بدون تغییر سورس.
**وضعیت:** این سند ممیزی ایستای مبتنی بر کد است، نه تأیید پیکسلی صفحات در مرورگر.

## خلاصهٔ اجرایی

پروژه یک هویت UI نسبتاً بالغ و مفصل تعریف کرده است: پوستهٔ روشن Mashal، Bootstrap RTL، primitiveهای مشترک `ak-*`، الگوی تفکیک‌شده برای List/Form/Details/Financial/Operational/Reports، و اصول مشخص برای اعداد مالی، اکشن‌ها، responsive و SPA lifecycle. در نمونه‌های بررسی‌شده، الگوهای پایه واقعاً به‌کار رفته‌اند؛ مثلاً فهرست فروش از header، KPI، فیلتر مشترک و جدول استفاده می‌کند، فرم بارگیری workflow حجیم را در گروه‌های مشخص نگه می‌دارد و Details فروش از KPI/summary/metadata/trace تفکیک‌شده استفاده می‌کند.

بااین‌حال، مهم‌ترین مسئلهٔ فعلی **نبود یک مرجع واحد کاملاً منطبق با runtime** است. چند سند معتبرِ طراحی، پوسته، رنگ، اندازه و ارتفاع متفاوت تعریف می‌کنند؛ حتی Master و قرارداد Mashal در مورد پوستهٔ Sidebar با هم تعارض دارند. از طرف دیگر، برنامه در `localhost:5000` در دسترس نبود و مرورگر در این نشست صفحهٔ بازِ برنامه نداشت؛ بنابراین contrast واقعی، فاصله‌ها، overflow، چیدمان 375/768/1024/1440px، stateهای تعاملی و تفاوت صفحات را نمی‌توانم صادقانه pixel-by-pixel تأییدشده اعلام کنم.

## پوشش بررسی‌شده

- `design-system/MASTER.md`، قرارداد Mashal، overrideهای Dashboard، List، Create/Edit، Details، Financial، Operational، Reports و Modals.
- `docs/UI-DESIGN-SYSTEM.md` و گزارش baseline قبلی `design-system/AUDIT.md`.
- پوستهٔ `_Layout.cshtml` و stylesheetهای اصلی؛ از جمله tokenها، PageFrame و Detail system.
- نمونه‌های نماینده: Dashboard، Payments/Create، Sales/Index، Sales/Details و Loading/Create.
- پیمایش ایستای پوشهٔ Razor و CSS.

شمارش فعلی پوشه‌ها: **۴۶۸ فایل Razor**، شامل حدود **۸۰ Index، ۵۱ Create، ۳۲ Edit و ۴۹ Details**؛ این شمارش نام فایل است و لزوماً معادل تعداد route یا صفحهٔ قابل‌دسترسی نیست. در scan متنی، ۴۷ فایل CSS منبع (به‌جز bundleهای دارای پسوند `.bundle.css`) پیدا شد. حدود ۲۶۹ رخداد `!important`، ۷۷۶ literal رنگ hex، ۸۹ رخداد `:focus-visible`، ۲۵ رخداد `prefers-reduced-motion` و ۲۲۱ رخداد `@media` شمارش شد. این‌ها شمارش occurrence هستند، نه تعداد selector یکتا یا اثبات پوشش/کیفیت بصری.

در scan متنی، هیچ `<style>` tag در Viewها پیدا نشد؛ inline style attribute در ۸ فایل و inline script در ۷۲ فایل پیدا شد. inline styleها شامل موارد کاربردی/داده‌محور مانند progress width و visibility حالت عملیاتی نیز هستند؛ بنابراین همهٔ آن‌ها به‌خودی‌خود تخلف یا مشکل بصری نیستند. inline scriptها نیازمند triage جداگانه‌اند تا موارد صرفاً bootstrap/صفحه‌ای از منطق طولانی و شکننده جدا شوند.

## یافته‌های قطعی و اولویت‌دار

### P0 — مراجع طراحی با هم و با runtime یکسان نیستند

1. `docs/UI-DESIGN-SYSTEM.md` پالت بنفش/سبز و اندازهٔ عنوان ۳۶px را توصیف می‌کند؛ درحالی‌که `design-system/MASTER.md` پالت سرمه‌ای/CTA آبی، عنوان فهرست ۲۵px و typography جدیدتر را بیان می‌کند. خود `design-system/AUDIT.md` نیز قدیمی‌بودن بخش‌هایی از سند UI را ثبت کرده است.
2. `design-system/MASTER.md` در بخش توکن‌ها مقادیری مثل `--primary-main: #173F73`، CTA `#1877F2`، canvas `#FCFCFC` و Header `56px / 52px` را نشان می‌دهد؛ `01-tokens.css` مقادیر پایهٔ دیگری دارد، از جمله `#1062D0`، `#F5F6FA` و Header `64px`.
3. `78-mashal-reference.css` روی body دارای کلاس `mashal-ui` مقادیر دیگری تعریف می‌کند: primary `#2d60c9`، canvas `#f5f6fa`، header `48px`، sidebar `240px` و فونت بدنه `15px`. `_Layout.cshtml` کلاس‌های `mashal-ui mashal-light` را روی body همهٔ routeها می‌گذارد؛ یعنی این اختلاف محدود به یک صفحه نیست.
4. `MASTER.md` می‌گوید «Sidebar dark»، درحالی‌که `design-system/mashal-unified-contract.md` و لایهٔ Mashal برای همهٔ صفحات shell روشن را تعریف می‌کنند. این تناقض دربارهٔ یک تصمیم کلیدی هویت بصری است.
5. یادداشت ترتیب CSS در `docs/UI-DESIGN-SYSTEM.md` با ترتیب جاری `_Layout.cshtml` همسان نیست؛ گزارش `design-system/AUDIT.md` نیز این مورد را قبلاً گوشزد کرده است.

**اثر:** طراح/توسعه‌دهنده نمی‌تواند فقط با خواندن یک سند، رنگ و سایز قطعی را با اطمینان انتخاب کند؛ تغییر کوچک ممکن است در variant نهایی بازنویسی شود یا صفحه‌ای ناخواسته از بقیه جدا بیفتد. پیش از هر بازطراحی گسترده، باید computed style پوستهٔ واقعی در صفحهٔ زنده مبنا قرار گیرد و یک سند canonical با runtime تطبیق داده شود؛ این ممیزی فعلاً هیچ‌کدام را تغییر نداده است.

### P1 — بار Cascade و پیچیدگی نگهداری بالاست

پوسته چند نسل CSS را کنار هم بار می‌کند: CSSهای پایه و legacy، لایه‌های `01–18`، لایه‌های `40+`، خانواده‌های feature، `70–78` و bundleها. فایل‌هایی مانند `site.css`، `boltz-shell.css`، `13-compat.css` و `17-system-forms.css` در Design System به‌عنوان legacy/bridge مشخص شده‌اند، ولی همچنان در مسیر بارگذاری دیده می‌شوند. گزارش قبلی نیز وجود hard-coded rules و `!important` در تعدادی از همین فایل‌ها را ثبت کرده است.

شمارش این مرحله ۲۶۹ `!important` در CSSهای منبع را نشان می‌دهد؛ کمتر از آمار گزارش قبلی که دامنه/زمان و احتمالاً روش شمارش متفاوت داشته است، پس اعداد مستقیم قابل مقایسه نیستند. مسئلهٔ اصلی تعداد به‌تنهایی نیست؛ ترتیب بارگذاری، selectorهای شرطی بر body/page class و overrideهای چندنسلی باعث می‌شوند پیش‌بینی ظاهر مؤثر از روی یک فایل دشوار شود.

### P1 — پوستهٔ نهایی فقط از CSS پایه قابل حدس نیست

کلاس `mashal-ui` به همهٔ صفحات authenticated/public body افزوده می‌شود و `78-mashal-reference.css` scope اصلی آن را تغییر می‌دهد. بنابراین تحلیل رنگ/فاصله بر اساس فقط `01-tokens.css` یا `MASTER.md` کافی نیست. علاوه بر آن، `78` و bundleها در زنجیرهٔ بارگذاری به هم نزدیک‌اند؛ ترتیب واقعی stylesheet و computed styles باید از صفحهٔ اجراشده تأیید شود، نه از comment داخل CSS به‌تنهایی.

### P1 — یکپارچگی primitiveها خوب است، اما کامل/اثبات‌شده نیست

در scan ایستا، `_AkPageHeader` در ۲۶۹ فایل، `_AkSearchFilter` در ۷۲ فایل و یکی از markerهای عمومی `ak-list-page`/`ak-form-page`/`ak-detail-page` در ۳۰۰ فایل دیده شد. این‌ها نشانهٔ reuse گسترده‌اند، اما شمارش text occurrence به‌تنهایی نمی‌گوید باقیمانده واقعاً ناسازگار است: بعضی صفحه‌ها component wrapper، layout ویژه، صفحهٔ چاپ، modal partial یا workflow مختص خود دارند. یک regex اولیه ۴۴ فایل را به‌عنوان دارای نام شبیه legacy علامت زد، اما چون این الگوها می‌توانند substring، comment یا کلاس خاص باشند، تا بررسی دستی آن‌ها را «بقایای تأییدشدهٔ legacy» محسوب نمی‌کنم.

### P2 — Dashboard ممکن است از تراکم تعریف‌شده فراتر رود

استاندارد Dashboard، ۴ KPI، حداکثر دو visualization و یک خلاصهٔ عملیاتی را ترجیح می‌دهد. در View جاری، چهار KPI و دو نمودار/visualization دیده می‌شود، به‌علاوهٔ `_DashboardInsights` و چند widget فهرستی قابل انتخاب (فعالیت، مخزن، هشدارها، خلاصه، تحلیل و حمل). بخشی از این‌ها اختیاری/قابل‌پنهان‌سازی‌اند؛ بدون state واقعی کاربر و screenshot نمی‌توان گفت viewport آغازین قطعاً شلوغ است. در مرحلهٔ بصری باید default widget set، fold اول و حالت empty/loading بررسی شود.

### P2 — در فرم‌های مالی و عملیاتی hierarchy درست اما تراکم بالاست

`Payments/Create` فرم چندحالته و انتخاب جهت/روش/منبع دارد و از بخش‌بندی، tab rail، label، validation و anti-forgery استفاده می‌کند. `Loading/Create` یک workbook چندردیفی، import، validation گروهی و خلاصهٔ مالی/عملیاتی دارد. این ساختار برای ERP قابل‌فهم است، ولی کیفیت نهایی به جزئیات واقعی وابسته است: آیا modeها، fieldهای شرطی، خطا، import progress، دکمهٔ ثبت و summary در اندازه‌های مختلف به‌خوبی جا می‌شوند؟ بررسی کد جواب پیکسلی آن نیست.

## ارزیابی مثبت الگوهای موجود

- جست‌وجو/فیلتر عمومی مالک مشترک و server-side دارد؛ این تصمیم خطر رفتار ناهمسان بین صفحات را کم می‌کند.
- قواعد List ترتیب identity، metadata، مقدار، status و action را روشن می‌کنند و mobile را به کارت‌های تکراری تبدیل نمی‌کنند.
- فرم‌ها label آشکار، گروه‌بندی بر اساس workflow، validation نزدیک فیلد و یک Save اصلی در footer را معیار قرار می‌دهند.
- Details بین identity/status، KPI، summary، تب‌ها، related records و metadata فنی تفکیک می‌کند؛ اطلاعات فنی حذف نمی‌شود و می‌تواند در Advanced قرار گیرد.
- قواعد مالی بر حفظ currency، FX، جهت debit/credit، running balance و جدایی semantics عملیاتی از ledger تأکید دارند.
- قواعد عملی مربوط به RTL و SPA، از جمله lifecycle دارایی صفحه و anti-forgery، به تجربهٔ پایدارتر کمک می‌کنند.

## محدودیت شواهد و آنچه هنوز انجام نشده

- مرورگر نشست خالی داشت؛ `http://localhost:5000` با `ERR_CONNECTION_REFUSED` پاسخ داد.
- برنامه را خودسرانه اجرا نکردم؛ محیط build قبلی به cache ناقص NuGet برخورد کرده بود و اجرای برنامه به PostgreSQL واقعی و تنظیم اتصال نیاز دارد.
- login انجام نشد، دادهٔ واقعی شرکت/کاربر دیده نشد، و هیچ صفحه‌ای در 375، 768، 1024 یا 1440px screenshot/اندازه‌گیری نشد.
- وضعیت Git به‌دلیل خطای `dubious ownership` قابل مشاهده نبود؛ هیچ فایل سورس یا تنظیم Git تغییر نکرد.
- در این مرحله تست contrast محاسباتیِ متن واقعی، keyboard/focus، screen reader، hover/disabled/loading/empty/error، جدول‌های حجیم، modalها، چاپ و ناوبری SPA انجام نشد.

پس ادعای «تمام صفحات pixel-by-pixel بررسی شدند» درست نیست. این سند فقط خط مبنای سورس و ناهماهنگی‌های طراحی را ثبت می‌کند.

## ترتیب پیشنهادی مرحلهٔ بعد

1. **ثبت حقیقت runtime:** از یک نشست واقعی و صفحهٔ authenticated، screenshot و computed layout پوسته، navbar/sidebar، رنگ‌های مؤثر، فونت و اندازه‌ها را در desktop ثبت کنیم؛ این مرحله مرجع‌های متناقض را روشن می‌کند.
2. **ماتریس صفحه‌ها:** همهٔ route/viewهای فعال را در خانواده‌های Dashboard، Master Data، List، Form، Details، Finance، Operations، Reports، HR، System و Print دسته‌بندی کنیم؛ نمونه‌برداری بر اساس risk و تفاوت component باشد، نه انتخاب تصادفی.
3. **بررسی تصویری نماینده‌ها:** در 1440 و 375px برای هر خانواده و 768/1024px برای صفحات پرتراکم/جدولی؛ قبل و بعد از scroll، فرم خطادار، modal باز، empty/loading و state فیلتر.
4. **پیکسلی/UX:** فاصله و alignment، grid، ارتفاع کنترل، typography، contrast، truncation، baseline اعداد/واحدها، RTL/LTR، focus/hover، overflow و visible action را با screenshot و نقاط اندازه‌گیری ثبت کنیم.
5. **گزارش اولویت‌بندی‌شده:** یافته‌ها با severity، صفحه/selector دقیق، اثر کاربری، شواهد screenshot و پیشنهاد اصلاح؛ بدون تغییر کسب‌وکار یا مالی.

## خلاصهٔ تصمیم

نقطهٔ شروع redesign، تولید component تازه نیست؛ ابتدا باید **variant و token مؤثر runtime را یک‌بار تثبیت** کنیم. بعد از آن می‌توان همهٔ صفحات را با baseline بصری ثابت و بر اساس خانواده/ریسک ممیزی کرد. گزارش حاضر مرحلهٔ اول است و برای تکمیل بخش pixel-level به برنامهٔ در حال اجرا و نشست مرورگر authenticated نیاز دارد.
