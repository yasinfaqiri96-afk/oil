# قرارداد یکپارچهٔ رابط PTG بر اساس Mashal UI Reference

این سند قرارداد اجرایی یکپارچه‌سازی رابط پروژه است. ساختار HTML/Razor و کامپوننت‌ها در کل سیستم مشترک می‌ماند؛ تفاوت ظاهری فقط در دو variant رسمی مرجع محدود است.

## منبع مرجع

- R01 تا R04: پوستهٔ روشن برای تنظیمات، فرم‌ها و تراکنش.
- R05 و R06: پوستهٔ آبی داشبورد و modal انتخاب ابزار.
- R07: پوستهٔ آبی برای دریافت مرتبط با فاکتور و کارت شخص/بانک همان جریان.
- فایل‌های مرجع در `.work/design-reference/pack/Mashal-UI-Reference-Pack/ui-references/` نگهداری شده‌اند و asset تولیدی نیستند.
- منبع component kit، شاخهٔ `design/mashal-reference-components` در مخزن مرجع است. kit به‌صورت runtime جداگانه بارگذاری نمی‌شود (token و adapter موازی حذف شد)؛ مقادیر اندازه‌گیری‌شدهٔ آن فقط در `78-mashal-reference.css` روی primitiveهای موجود `ak-*` اعمال شده‌اند.

## قرارداد مشترک همهٔ صفحات

- ساختار صفحه از shell موجود، `ak-*` components، Bootstrap 5 RTL و Razor فعلی استفاده می‌کند.
- عنوان صفحه، section head، فرم، table، status، card، modal، dropdown، pager و action bar باید از primitiveهای مشترک استفاده کنند؛ selector صفحه‌ای جدید فقط برای layout اختصاصی همان صفحه مجاز است.
- منطق business، route، binding، validation، permission، anti-forgery، POST، SPA lifecycle، ledger و inventory تغییر نمی‌کند.
- فونت همان IRANSans با fallback فعلی پروژه است؛ متن و دادهٔ دامنهٔ نفت و گاز حفظ می‌شود.
- کنترل‌ها فشرده، خوانا و RTL هستند؛ radius کوچک، border ظریف، shadow محدود و بدون gradient/glow استفاده می‌شود.
- رنگ وضعیت فقط برای state واقعی استفاده می‌شود؛ دکمهٔ اصلی یک accent مشخص دارد.
- در 375، 768، 1024 و 1440 پیکسل نباید صفحهٔ اصلی horizontal overflow داشته باشد.

## پوسته

یک پوستهٔ واحد `mashal-light` (R01–R04) برای همهٔ صفحات، از جمله داشبورد و دریافت ذیل فاکتور. پوستهٔ آبی R05–R07 حذف شد؛ از آن تصاویر فقط کامپوننت‌های محتوایی (کارت‌های ابزارک، مودال انتخاب ابزار، کارت شخص/بانک و آواتارها) با رنگ اصلی `#2D60C9` استفاده می‌شوند.

- آیکن‌ها: Iconsax Linear (همان خانوادهٔ منوهای مرجع) در `_NavIconSprite.cshtml`.
- آواتار: R01 اپراتور (`images/mashal-profile-avatar.png`)، R05 placeholder خاکستری برای ردیف اشخاص، R07 آواتار خطی (`images/mashal-receipt-avatar.png`).
- کارت‌های آمار تصویر تزئینی ندارند.

## وضعیت فعلی و مسیر اصلاح

- `78-mashal-reference.css` آخرین لایهٔ runtime است و باید تنها محل overrideهای مرجع باشد؛ قواعد `!important` قدیمی که هدر/منو را قفل می‌کردند (site.css، 13-compat، 45-akaunting) حذف شدند.
- bundleهای Production باید پس از هر تغییر CSS با `PtgBuildBundles=true` بازسازی شوند.
- صفحات جدید نباید CSS مستقل برای بازتعریف global componentها اضافه کنند.
