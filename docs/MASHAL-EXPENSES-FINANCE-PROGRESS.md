# وضعیت کار مصارف و مالی — ۲۰۲۶-۱۰-۱۰

شاخه: `fix/mashal-expenses-finance`؛ worktree: `/workspace/mashal-expenses`.

## پیاده‌سازی‌های ثبت‌شده

- `6439f31`: انتخاب بارگیری برای مصارف گروهی، قواعد تسویه و ثبت حسابداری/Audit هر سهم.
- `db6dca3`: چهار نرخ روز تخصیص با دقت ۱۲، migration با Down محافظت‌شده؛ نرخ و مبلغ قدیمی بازنویسی نمی‌شود.
- `0f1482e`: مصرف گروهی تازه کرایهٔ مستقل قدیمی بارگیری را از تجمیع/P&L حذف نمی‌کند.
- `2bcfd3c`: حساب مختلط و نرخ تاریخی چندارزی مصرف نقدی حفظ می‌شود.
- `e0a7f31`: مستندات رفتار واقعی Platts، USD/RUB، نقد مستقل و ثبت گروهی.
- `f725ff1` و `2fe5b85`: جلوگیری از پرداخت تازه برای مصرف قبلاً نقدپرداخت‌شده و جلوگیری از بدهی مصنوعی برای طرفِ صرفاً ردیابی‌شده.
- `fecba93`: تقسیم مصرف حمل چندمنبعی به قراردادهای واقعی؛ OperationCount یک حمل یک می‌ماند؛ جمع دقیق سهم‌های مالی.
- `55e1fc9`: fallback بهای Verified از Journal دقیق COGS، وقتی فروش مستقیم هیچ مصرف Pool ندارد؛ جمع Pool و Journal انجام نمی‌شود. فرمول اقتصادی/Estimated قرارداد به‌زور با آن یکسان نشده است.
- `c21aecb`: قفل مرتب و بازبینی مجازبودن منابع داخل تراکنش؛ قفل‌های بارگیری با یک query گروهی گرفته می‌شوند.
- `49f878d`: Dispose تراکنش گروه بعد از Commit/Rollback.
- `6053537`: تست واقعی Up/Down در دیتابیس موقت PostgreSQL و تطبیق EF model/snapshot.
- `fc4d0a0`: تست PG درخواست تکراری و خطای تزریقی بعد از اولین Journal واقعی، با بررسی صفر ماندن تمام اثرهای گروه ناموفق.

خدمت مشترک منبع `5360b13` در شاخهٔ اصلی ادغام شود؛ نسخهٔ معادل در این شاخه `bd7c62b` است و نباید دوباره ادغام شود.

## وضعیت بررسی

در این worktree Build سنگین مستقل اجرا نشده؛ هماهنگ‌کننده مالک Build یکپارچهٔ مرحلهٔ ۳/۴ و اجرای تست‌هاست. افزودن تست به معنی قبولی آن نیست. فیلترهای لازم: `ExpenseAccountingAdapterTests`، `ExpensesControllerTests`، `ExpenseLedgerPosterTests`، `PaymentsControllerTests`، `PaymentAccountingAdapterTests`، `SupplierPaymentAllocationTests`، `SupplierPaymentAllocationAccountingAdapterTests`، `SupplierAllocationFxMigrationTests`، `PurchaseAggregationServiceTests` و `ProfitAndLossServiceTests`.

## ادامهٔ دقیق

1. ادغام commitهای این شاخه به ترتیب؛ حفظ تغییر جدید `df96f9a` که فروش دارای سهم بی‌بها را NeedsReview می‌خواند.
2. Build پروژهٔ تست همراه وابستگی Web یک بار؛ رفع خطای واقعی کامپایل در شاخهٔ مالک و اجرای هدفمند روی PostgreSQL محلی با `PTG_TEST_POSTGRES_ADMIN` خصوصی.
3. ثبت خروجی TRX و تعداد واقعی تست‌ها؛ اجرای تست migration باید فقط دیتابیس با پیشوند `DatabaseSafetyGuard.AccountingTestDatabasePrefix` را بسازد و حذف کند.
4. اجرای benchmark مصارف ۱٬۰۰۰ بارگیری با حسابداری فعال توسط QA. ثبت Loading در نسخهٔ اولیهٔ گروهی پشتیبانی نمی‌شد؛ عدد baseline برای آن سناریو ساخته نشود.
5. UX مالک اصلاح escaping داده‌های نمایشی و pagination پیش‌نمایش مصارف است؛ Map و درخواست کامل حفظ شوند.
6. بازبینی Sync قیمت حمل در main جدید: تغییر قیمت نباید snapshot بار فروخته‌شده/بسته را بازنویسی کند. مالک Backfill اصلاح محافظت‌شده و تست آن را انجام می‌دهد.

هیچ دیتابیس اصلی، Backfill عملیاتی، Reset یا نشر روی سرور انجام نشده است. دغدغهٔ تاریخچهٔ فرمول‌های Estimated قرارداد از Verified COGS جداست و بدون شواهد تاریخی کافی ادعای اصلاح همهٔ گزارش‌های گذشته نمی‌شود.
