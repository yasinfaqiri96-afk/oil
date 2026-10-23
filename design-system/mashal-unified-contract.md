# قرارداد یکپارچهٔ رابط PTG بر اساس Mashal UI Reference

این سند قرارداد اجرایی یکپارچه‌سازی رابط پروژه است. ساختار HTML/Razor و کامپوننت‌ها در کل سیستم مشترک می‌ماند؛ تفاوت ظاهری فقط در دو variant رسمی مرجع محدود است.

## منبع مرجع

- R01 تا R04: پوستهٔ روشن برای تنظیمات، فرم‌ها و تراکنش.
- R05 و R06: پوستهٔ آبی داشبورد و modal انتخاب ابزار.
- R07: پوستهٔ آبی برای دریافت مرتبط با فاکتور و کارت شخص/بانک همان جریان.
- فایل‌های مرجع در `.work/design-reference/pack/Mashal-UI-Reference-Pack/ui-references/` نگهداری شده‌اند و asset تولیدی نیستند.

## قرارداد مشترک همهٔ صفحات

- ساختار صفحه از shell موجود، `ak-*` components، Bootstrap 5 RTL و Razor فعلی استفاده می‌کند.
- عنوان صفحه، section head، فرم، table، status، card، modal، dropdown، pager و action bar باید از primitiveهای مشترک استفاده کنند؛ selector صفحه‌ای جدید فقط برای layout اختصاصی همان صفحه مجاز است.
- منطق business، route، binding، validation، permission، anti-forgery، POST، SPA lifecycle، ledger و inventory تغییر نمی‌کند.
- فونت همان IRANSans با fallback فعلی پروژه است؛ متن و دادهٔ دامنهٔ نفت و گاز حفظ می‌شود.
- کنترل‌ها فشرده، خوانا و RTL هستند؛ radius کوچک، border ظریف، shadow محدود و بدون gradient/glow استفاده می‌شود.
- رنگ وضعیت فقط برای state واقعی استفاده می‌شود؛ دکمهٔ اصلی یک accent مشخص دارد.
- در 375، 768، 1024 و 1440 پیکسل نباید صفحهٔ اصلی horizontal overflow داشته باشد.

## Variantها

### `mashal-light`

برای تنظیمات، صفحات عملیاتی و تراکنش عادی: canvas روشن، surface سفید، sidebar روشن، کنترل‌های فشرده و primary action آبی روشن (`#2D60C9`).

### `mashal-legacy`

فقط برای داشبورد و دریافت مرتبط با فاکتور: topbar/sidebar آبی (`#007DB2`)، active navigation تیره‌تر، کارت‌های سفید و metricهای dashboard با چگالی R05/R07.

این دو variant نباید با هم ترکیب شوند؛ primitiveها مشترک‌اند و فقط token/layout variant تغییر می‌کند.

## وضعیت فعلی و مسیر اصلاح

- `78-mashal-reference.css` آخرین لایهٔ runtime است و باید تنها محل overrideهای مرجع باشد.
- bundleهای Production باید پس از هر تغییر CSS با `PtgBuildBundles=true` بازسازی شوند.
- صفحات جدید نباید CSS مستقل برای بازتعریف global componentها اضافه کنند.
