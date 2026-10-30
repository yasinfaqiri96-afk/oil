# قرارداد یکپارچهٔ رابط PTG — پورت زبان بصری

این سند قرارداد اجرایی یکپارچه‌سازی رابط پروژه است. ساختار HTML/Razor و کامپوننت‌ها در کل سیستم مشترک می‌ماند؛ wrapperهای legacy فقط تا وقتی مصرف‌کنندهٔ runtime دارند حفظ می‌شوند و هدف بصری جداگانه نیستند.

## منبع مرجع

- گزارش «گزارش-دیزاین-سیستم.md» مرجع زبان بصری و رفتار نمونه‌هاست؛ گزارش متعلق به یک محصول React/Tailwind است و asset یا معماری آن به PTG منتقل نمی‌شود.
- هویت و محتوای PTG، IRANSans، MVC/Razor، Bootstrap RTL، کلاس‌های `ak-*`، routeها و workflowهای عملیاتی باقی می‌مانند.
- assetهای فعلی Mashal و hookهای `mashal-*` صرفاً وابستگی runtime فعلی‌اند؛ لوگو/نام محصول مرجع جایگزین برند PTG نمی‌شود.

## قرارداد مشترک همهٔ صفحات

- ساختار صفحه از shell موجود، `ak-*` components، Bootstrap 5 RTL و Razor فعلی استفاده می‌کند.
- عنوان صفحه، section head، فرم، table، status، card، modal، dropdown، pager و action bar باید از primitiveهای مشترک استفاده کنند؛ selector صفحه‌ای جدید فقط برای layout اختصاصی همان صفحه مجاز است.
- منطق business، route، binding، validation، permission، anti-forgery، POST، SPA lifecycle، ledger و inventory تغییر نمی‌کند.
- فونت همان IRANSans با fallback فعلی پروژه است؛ متن و دادهٔ دامنهٔ نفت و گاز حفظ می‌شود.
- کنترل‌ها خوانا و RTL هستند؛ کنترل ۴۰px، radius کنترل ۸px، border ظریف و shadow محدود. CTA اصلی آبیِ ساده و پرکنتراست است؛ گرادیان سراسری و glow تزئینی ممنوع.
- رنگ وضعیت فقط برای state واقعی استفاده می‌شود؛ دکمهٔ اصلی یک accent مشخص دارد.
- در 375، 768، 1024 و 1440 پیکسل نباید صفحهٔ اصلی horizontal overflow داشته باشد.

## زبان بصری هدف واحد

- Canvas روشن `#F3F4F8`، سطح سفید، متن `#334155` و مرز `#E2E8F0`؛ teal `#007DB2`/`#006088` فقط برای sidebar و آبی `#2563EB`/`#1D4ED8` برای action و focus.
- Sidebar teal با متن روشن؛ کنترل استاندارد ۴۰px، اکشن ۴۴/۴۸px، radius کنترل ۸px و کارت ۱۶px؛ shadow نرم و فقط روی سطحی که از canvas جدا می‌شود.
- وضعیت از palette معنایی مستقل می‌آید؛ آبی روشن `#3B82F6` با متن سفید برای CTA استفاده نشود، چون contrast AA را تأمین نمی‌کند.
- `mashal-light` و `mashal-legacy` wrapperهای route هستند، نه دو هدف طراحی. تا حذف واقعی مصرف‌کننده‌ها حفظ می‌شوند؛ استثناهای فعال page-specific به‌جای پاک‌کردن کورکورانه مستند و در مرورگر بازبینی می‌شوند.

## وضعیت فعلی و مسیر اصلاح

- فازهای ۱ تا ۶ برای shell، dashboard/details، فرم/جدول، modal/navigation و گزارش/نمودار در کد اعمال شده‌اند؛ پاکسازی نهایی، overrideهای عمومیِ متعارض را به tokenهای مشترک برگرداند.
- مرور تصویری واقعی در viewportهای هدف، print/PDF/Excel و صفحه‌های حساسِ route-specific پیش از اعلام visual sign-off باقی است؛ از بررسی ایستای CSS نتیجهٔ screenshot-level گرفته نمی‌شود.
- `78-mashal-reference.css` آخرین لایهٔ runtime است؛ rule تازهٔ سراسری فقط در مالک shared مربوطه یا همین لایهٔ مرجع، با scope محدود، اضافه شود.
- CSS/JS قدیمی حذف نمی‌شود مگر این‌که جست‌وجوی runtime مصرف‌کنندهٔ زنده‌ای نیابد و UI verification بگذرد.
- bundleهای Production باید پس از هر تغییر CSS با `PtgBuildBundles=true` بازسازی شوند.
- صفحهٔ جدید نباید CSS مستقل برای بازتعریف global componentها اضافه کند.
