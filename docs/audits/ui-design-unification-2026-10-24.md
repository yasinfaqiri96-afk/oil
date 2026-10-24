# نتیجهٔ اصلاح هماهنگی طراحی — ۲۰۲۶-۱۰-۲۴

اصلاحات روی همان checkout و بر مبنای طراحی روشن فعلی Mashal و primitives موجود `ak-*` انجام شد. منطق مالی، موجودی، نرخ، وزن، Ledger، Entity، Migration، Controller، Service، route و permission تغییر نکرد. چیزی commit، publish یا deploy نشده است.

## نتیجهٔ اجرا

- Web و graph پروژهٔ تست با `--no-restore` ساخته شدند: **صفر خطا**؛ ۱۸ هشدار در کدهای موجود ثبت شد.
- **۲۲۲ تست منتخب** در آخرین اجرای پس از تغییرات موفق شدند؛ صفر شکست و صفر skip. full suite اجرا نشد.
- **۱۲ سناریوی Chromium/Playwright** با CSS و JS واقعی checkout موفق شدند؛ خطای JavaScript ثبت نشد.
- جهت ۸۶ View از RTL ثابت به زبان جاری منتقل شد. تست مشترک، نبود `dir="rtl"` ثابت در همهٔ Viewها و حفظ isolation اعداد را بررسی می‌کند.
- فایل‌های منبع تغییرکرده UTF-8/LF هستند؛ syntax پنج فایل JavaScript مرتبط و `git diff --check` بررسی شد.

**مرز اعتبار:** آزمون‌های مرورگر روی fixture کامپوننت‌ها با ترتیب assets واقعی `_Layout` و `_ModalLayout` اجرا شدند؛ این‌ها screenshot صفحات واردشده به سیستم نیستند. نسخهٔ محلی فعالی شناسایی نشد و نشست واقعی با دادهٔ عملیاتی در اختیار نبود. هیچ فرم مالی واقعی ارسال نشده است. بنابراین تأیید بصری تمام صفحات، همهٔ وضعیت‌های داده و همهٔ نقش‌های کاربری ادعا نمی‌شود.

## وضعیت یافته‌های ممیزی

| مورد | اصلاح انجام‌شده | تأیید |
|---|---|---|
| D01 | CSS و JS فرم انتقال در چرخهٔ page asset ثبت شدند. | تست قرارداد Create/Edit و remount کامپوننت در SPA؛ ناوبری زندهٔ app هنوز بررسی نشده است. |
| D02 | Create و Edit از initializer و stylesheet یکسان استفاده می‌کنند. | تست قرارداد assets، build و تست رفتار فرم مشترک. |
| D03 | نام مشتری، مبلغ و ارز نمایشی با انتخاب، clear و reset همگام می‌شوند؛ محاسبهٔ مالی اضافه نشد. | سناریوی انتخاب، تغییر، پاک‌کردن، reset و remount مرورگر. |
| D04 | لایه‌های فرم و Mashal، scope محتوای مشترک و initializerهای مرتبط در سند مودال نیز فراهم شدند؛ طبقه‌بندی workspace مالی همسان شد. | تست ترتیب assets و مقایسهٔ computed styles دو layout در fixture. |
| D05 | primary action، Save و focus به palette مشترک متصل شدند؛ overrideهای اجباری متعارض در لایه‌های قدیمی دکمه حذف شدند. | رنگ هر دو اکشن `rgb(45,96,201)` و کنتراست ۵٫۷۹ در نمونه. |
| D06 | اندازه و وزن جدول از قرارداد ۱۴px/۵۰۰ پیروی می‌کند؛ `!important`های متعارض محدوداً حذف شدند. اعداد LTR/tabular باقی ماندند. | computed style سرستون، رقم و parity مودال. |
| D07 | success/warning/info معنا و رنگ مستقل دارند؛ specificity آن‌ها در برابر reset عمومی اصلاح شد. | سه پس‌زمینهٔ متفاوت؛ کنتراست متن نمونه‌ها بیش از ۴٫۵. |
| D08 | فیلتر اصلی همچنان server/query است؛ فیلتر محلی grid حفظ و صریحاً «فیلتر نمایش» معرفی شد، با state و Clear مشترک و توضیح scope خروجی. | رفتار فیلتر و clear؛ قرارداد و مستندات هماهنگ شدند. |
| D09 | کنترل‌های فیلتر ستون در موبایل پنهان نمی‌شوند؛ محدودیت فعال، متن و Clear قابل‌مشاهده دارد. | سناریوی فیلتر در desktop، تغییر به ۳۹۰px و پاک‌کردن. |
| D10 | نقش اشتباه listbox به region تغییر کرد؛ رابطهٔ کنترل/ظرف، expanded state، ورود کیبورد، حرکت میان دکمه‌ها و بازگشت focus با Escape اضافه شد. | سناریوی واقعی کیبورد؛ screen reader واقعی هنوز اجرا نشده است. |
| D11 | جهت محتوای ۸۶ فایل از context زبان تأمین می‌شود؛ CSS اعداد مستقل ماند. | build تمام Razorها، تست پیمایش همهٔ Viewها و fixture انگلیسی. |
| D12 | متن‌های شناسایی‌شدهٔ فرم انتقال، picker و پیام ذخیرهٔ داشبورد و اکشن‌های نمونهٔ Dispatch با مسیر ترجمهٔ مشترک هماهنگ شدند. | build و تست‌های localization؛ این اصلاح ادعای ترجمهٔ کامل تمام متن‌های قدیمی نیست. |
| D13 | Cancel انتخاب‌گر داشبورد خنثی شد؛ جهت اجباری LTR از ردیف اکشن آن حذف شد. | کد CSS و build؛ picker روی داده/نشست واقعی بررسی نشده است. |
| D14 | مستند AK و MASTER با palette، type، assets و تفاوت فیلتر اصلی/نمایش فعلی هماهنگ شدند. | بررسی منبع؛ گزارش اولیه به‌عنوان snapshot قبل از اصلاح حفظ شده است. |
| D15 | `82-customer-transfer.css` فقط ترکیب صفحه را تعیین می‌کند؛ input، label، error، number و action از primitives مشترک استفاده می‌کنند. | build و قرارداد فیلدهای فرم؛ name و binding فیلدها حفظ شدند. |
| D16 | header نوع Detail را از قرارداد صریح یا identity می‌گیرد؛ GroupDetails، PreSaleDetails و ActiveDetails صریحاً مشخص شدند. | build و تست‌های ساختاری صفحات مرتبط؛ route تغییر نکرد. |
| D17 | سه regression test قرارداد مشترک اضافه شد؛ تست RTL قدیمی به بررسی دقیق حالت دو‌زبانه تغییر کرد. بررسی computed cascade به تست‌های مرورگری اضافه شد. | ۲۲۲ تست منتخب و ۱۲ سناریوی browser. |

فیلتر نمایش محلی حذف نشده است؛ کاربر همچنان همان قابلیت را دارد. تفاوت آن با فیلتر اصلی و اثر آن بر export اکنون در UI آشکار است. افزودن فیلتر سمت سرور یا تغییر queryها در این کار انجام نشد.

## ریسک‌های مرورگری و محدودهٔ بسته‌شده

| مورد | نتیجه |
|---|---|
| V01 — بریدگی منوی انتهای جدول | در fixture جدول ۳۰ ردیفی با viewport محدود، منوی آخرین ردیف داخل صفحه و واقعاً قابل‌رسم بود؛ برای تمام محتوای واقعی منو تضمین نشده است. |
| V02 — لمس | منوی static در context واقعی touch شبیه‌سازی‌شده قابل‌مشاهده بود و target حداقل ۴۴×۴۴ داشت. |
| V03 — footer و کیبورد موبایل | target و layout موبایل بررسی شد؛ کیبورد مجازی سیستم‌عامل و safe area دستگاه واقعی هنوز بررسی نشده است. |
| V04 — چاپ | در حالت print، اکشن فرم پنهان و محدودیت اسکرول جدول آزاد می‌شود. PDF اسناد واقعی و جدول چندصفحه‌ای نیازمند بررسی جداست. |
| V05 — کنتراست/دسترس‌پذیری | متن success/warning/info و اکشن اصلی نمونه، کنتراست حداقل ۵٫۳۳ داشتند؛ focus کیبورد بررسی شد. این گواهی WCAG کل برنامه نیست. |
| V06 — هزینهٔ CSS | خانوادهٔ کنترل موازی انتقال کوچک شد و overrideهای متعارض محدوداً حذف شدند. benchmark performance/CLS یا مقایسهٔ شبکهٔ production انجام نشد؛ صرف تعداد فایل دلیل حذف فایل‌های سازگار نبود. |
| V07 — pager نتیجهٔ خالی | متن نامعتبر ۱–۰ به ۰–۰ اصلاح و با فیلتر بدون نتیجه تست شد. |

دو فرم مشخص‌شده در گزارش اولیه نیز antiforgery صریح دریافت کردند؛ guardهای سمت سرور و token مستقل idempotency حفظ شدند. این تغییر در View است و بررسی جامع امنیت یا اثبات خطای ۴۰۰ قبلی محسوب نمی‌شود.

## شواهد مرورگر

نتیجهٔ ماشینی: [results.json](ui-unification-proof/results.json). نمونه‌ها دادهٔ آزمایشی دارند و هیچ دادهٔ مشتری از پایگاه داده استفاده نشده است.

| صحنه | تصویر |
|---|---|
| کامپوننت‌های صفحهٔ کامل، desktop | [مشاهده](ui-unification-proof/components-desktop.png) |
| همان نقش‌ها با assets سند مودال | [مشاهده](ui-unification-proof/components-modal.png) |
| کامپوننت‌ها در عرض ۳۹۰ | [مشاهده](ui-unification-proof/components-mobile.png) |
| فیلتر نمایش فعال و نتیجهٔ خالی در موبایل | [مشاهده](ui-unification-proof/local-filter-mobile.png) |

اندازه‌های مشاهده‌شده: عنوان ۲۰px، سرستون و عدد ۱۴px، وزن سرستون ۵۰۰، ارتفاع کنترل desktop برابر ۳۸px و radius برابر ۳px؛ ورودی موبایل ۱۶px باقی می‌ماند. در عرض‌های ۳۶۰، ۷۶۸، ۱۰۲۴ و ۱۴۴۰ برای fixture کامپوننت‌ها overflow سند دیده نشد.

## اعتبارسنجی قابل‌تکرار

```powershell
.\scripts\dev-verify.ps1 -Web -Filter 'FullyQualifiedName~ShellViewStructureTests|FullyQualifiedName~EnglishUiLocalizationStructureTests|FullyQualifiedName~ContractJourneyViewStructureTests|FullyQualifiedName~FinancialReportsViewStructureTests|FullyQualifiedName~GroupUnloadViewStructureTests|FullyQualifiedName~TransportWorkflowViewStructureTests|FullyQualifiedName~PartnershipStatementViewTests|FullyQualifiedName~PartyStatementFilterViewStructureTests|FullyQualifiedName~AuthLoginViewStructureTests|FullyQualifiedName~UnifiedUiContractTests|FullyQualifiedName~FiscalYearUiTests|FullyQualifiedName~PartyStatementTradeViewTests'
```

مرورگر با مهارت Playwright و اسکریپت موقت اجرا شد؛ fixture از فایل‌های CSS/JS همین checkout استفاده کرد. script آزمون در مسیر موقت سیستم است و جزو کد برنامه نیست؛ شواهد پذیرفته‌شده در پوشهٔ این گزارش نگهداری شده‌اند.

## بررسی باقیمانده برای تأیید نهایی سراسر سیستم

بررسی صفحه‌به‌صفحه با نشست واقعی برای همهٔ ماژول‌ها، نقش‌های محدود، داده‌های بلند، stateهای خطا/خالی/مشغول، direct/SPA/back-forward، فرم‌های واقعی `modal=1`، چاپ چندصفحه‌ای، screen reader، zoom و کیبورد مجازی دستگاه هنوز لازم است. ورود و ارسال عملیات تجاری واقعی در این مرحله انجام نشده است. اصلاحات کدی مشترک آمادهٔ بازبینی‌اند؛ این محدودیت نباید با تأیید کامل بصری تمام سیستم اشتباه گرفته شود.
