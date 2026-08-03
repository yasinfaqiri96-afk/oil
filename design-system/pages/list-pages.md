# List Pages

این فایل برای Index/Listهای عملیاتی و master data است.

## ساختار

1. `_AkPageHeader`
2. 3–4 KPI اختیاری، فقط برای list تصمیم‌ساز
3. یک `.ak-list` شامل toolbar
4. `_AkSearchFilter` + export/secondary actions
5. `.ak-table-wrap > .ak-table`
6. pager/footer

مراجع: `Sales/Index`, `Contracts/Index`, `Loading/Index`, `Payments/Index`.

## Header و Actions

- primary action: Create/Add در header.
- Export، Pre-sale، bulk action و عملیات کم‌تکرار secondary هستند.
- در هر ردیف فقط identity link و menu/یک action روشن.
- destructive action در menu با confirmation.

## KPI و چگالی

- list ساده KPI ندارد.
- list عملیاتی می‌تواند 3–4 KPI واقعی داشته باشد.
- toolbar و table باید نزدیک باشند.
- row height فشرده ولی touch/focus قابل استفاده بماند.

## Table

- ترتیب: selection، identity، metadata، عدد/مقدار، status، actions.
- ستون identity رشدپذیر؛ ستون عدد و action ثابت.
- اعداد با `.ak-num`، نام با `.ak-name` و status با `.ak-status`.
- متن دوم با `.ak-section-desc` یا `.ak-muted`.
- mobile: ستون‌های کم‌اهمیت پنهان/stack شوند فقط اگر داده از مسیر Details قابل دسترسی باشد؛ table به کارت‌های تکراری تبدیل نشود.
- horizontal scroll فقط داخل `.ak-table-wrap`.

## Search و Filter

- فقط `_AkSearchFilter`.
- query string منبع حقیقت و filter server-side است.
- scopeهای route با Hidden حفظ شوند.
- sorting/paging موجود تغییر نکند.

## Empty/Loading

- `.ak-empty` با پیام کوتاه و action واقعی.
- loading باید ساختار table را نپراند.
- zero result فیلتر با empty database اشتباه نشود.

## Reuse

- `_AkPageHeader`
- `_AkSearchFilter`
- `_ExportMenu`
- `_PagedListFooter` / `_OperationsListFooter`
- `_PersonCell`, `_Capsule`, `_StatusBadge`
- `.ak-row-menu`, `tables.js`, `list-toolbar-row.js`

## Anti-pattern

- filter client-side موازی
- Bootstrap card برای هر ردیف desktop
- action buttonهای دائماً برجسته در همه ردیف‌ها
- ستون بدون label/aria
- عدد بدون واحد یا currency
- table جداگانه فقط برای ظاهر

