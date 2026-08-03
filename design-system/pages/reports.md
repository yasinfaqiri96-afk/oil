# Reports

## ساختار

### Report Hub

- گروه‌بندی گزارش‌ها با `ptg-tabs-rail`.
- tile فقط برای مقصد گزارش واقعی.
- عنوان و توضیح کوتاه؛ artwork تزئینی کنترل‌شده.

### Report Page

1. `_AkPageHeader`
2. parameter/filter toolbar
3. export نزدیک پارامترها
4. 3–5 KPI summary
5. chart یا table اصلی
6. توضیح روش محاسبه در صورت نیاز

مراجع: `Reports/Index`, `Reports/CompanyOverview`.

## Parameters

- search عمومی با `_AkSearchFilter`.
- پارامتر گزارش با `.ak-report-parameters`؛ این دو را ادغام نکن.
- From/To، company، contract، product و currency فقط اگر backend پشتیبانی می‌کند.
- active filterها و scope در query string باقی بمانند.

## KPI و عدد

- summary باید پاسخ اصلی گزارش را بدهد.
- واحد و currency در header/label روشن.
- total، opening، movement و closing semantics مخلوط نشوند.
- مقدار تخمینی/operational با formal accounting label جدا شود.

## Table و Chart

- table جایگزین یا مکمل chart باشد.
- header ثابت/خوانا و اعداد tabular.
- export با همان فیلتر فعال تولید شود.
- chart series از توکن موجود و legend واضح.
- no-data و error state جدا.

## Responsive

- parameter bar wrap می‌شود؛ fieldها حداقل عرض خوانا دارند.
- table داخل wrapper scroll می‌شود.
- KPI در tablet دو ستون و mobile یک ستون.
- chart labelها در عرض کوچک کاهش یابند، نه خود داده.

## Reuse

- `_AkPageHeader`, `_AkSearchFilter`, `_ExportMenu`
- `.ak-report-parameters`
- `StatCard`, `.ak-stat-grid`
- `.ak-table`, `.ak-table-wrap`
- `tabular-export.js`, `ptg-tabs.js`

## Anti-pattern

- chart بدون تصمیم/سؤال مشخص
- union یا جمع داده‌های مالی متفاوت فقط برای نمایش
- filter نمایشی بدون backend
- export با scope متفاوت از صفحه
- رنگ زیاد برای series کم
- card hub به‌عنوان الگوی همه صفحات

