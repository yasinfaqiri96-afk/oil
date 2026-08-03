# Details Pages

## ساختار canonical

1. `.ak-detail-page` و `data-ak-detail-v2="true"`
2. `_AkPageHeader` با identity، status و action hierarchy
3. `_DetailKpiStrip` با 3–5 KPI
4. summary/statement
5. tabs یا sectionهای اصلی
6. `_OperationsDetailMore` برای timeline/related/advanced
7. `_DetailActionBar`

مراجع: `Sales/Details`, `Payments/Details`, shared AK Detail partials.

## Header و Actions

- عنوان رکورد کوتاه؛ context حداکثر سه fact.
- status کنار title.
- یک primary next action.
- Edit/Print/Export/Cancel در secondary یا kebab.
- Back و returnUrl حفظ شود.

## KPI

- 3 تا 5 مقدار تصمیم‌ساز.
- برای صفحات مالی: total، paid/received، outstanding، balance.
- برای عملیات: quantity، delivered، remaining، shortage/status.
- KPI تکراری با summary حذف شود؛ خود داده حذف نشود.

## Content hierarchy

- کارت اول: هویت/اطلاعات اصلی.
- کارت دوم: statement یا وضعیت عملیاتی.
- metadata فنی و شناسه‌ها در Advanced.
- linked records و timeline در componentهای shared.
- card nesting و elevation تودرتو ممنوع.

## Grid و Responsive

- desktop دو ستون برای دو summary هم‌وزن.
- section اصلی سنگین تمام‌عرض.
- زیر 992px تک‌ستون.
- tab rail در mobile scroll افقی دارد.
- اعداد و actionها wrap کنترل‌شده داشته باشند.

## Tables و records

- table جزئیات از `.ak-detail-table` یا record-list موجود.
- total row برجسته ولی بدون رنگ نمایشی.
- empty state scoped به همان section.
- pager جزئیات از `_DetailPager`.

## Reuse

- تمام partialهای خانواده `_Detail*`
- `_OperationsDetailMore`
- `_RelatedRecords`, `_DetailTimeline`
- `.ak-list`, `.ak-summary`, `.ak-status`, `.ak-num`
- `detail-record-pager.js`, `ptg-tabs.js`

## Anti-pattern

- ساخت header یا tabs اختصاصی
- نمایش همه metadata در بالای صفحه
- ده‌ها KPI همزمان
- پنهان‌کردن action مهم
- حذف trace/accounting fields
- تغییر route یا calculation برای layout
- کپی ساختار عظیم Shipment برای Details ساده

