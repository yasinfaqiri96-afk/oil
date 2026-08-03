# Create / Edit Forms

## ساختار

1. `_AkPageHeader` با Back
2. validation summary
3. 3–5 `.ak-form-section`
4. `.ak-form-grid`
5. review/summary فقط برای workflowهای مالی یا چندردیفی
6. `_AkFooterActions`

مراجع: `Sales/Create`, shared `_CreatePageShell`, `_EditPageShell`.

## Header و Actions

- عنوان فعل‌محور و دقیق.
- Save فقط در footer canonical؛ دکمه دوم Save در header نساز.
- Cancel خنثی و به returnUrl/Details معتبر.
- Quick Create و modal shell قرارداد موجود خود را حفظ می‌کنند.

## Grouping

- گروه‌بندی با workflow، نه صرفاً نوع input.
- label همیشه آشکار و نزدیک input.
- required marker فقط برای فیلد واقعاً الزامی.
- فیلد کم‌استفاده به Advanced؛ هیچ فیلد backend حذف یا پنهان نشود.
- description کوتاه در `_AkSectionHead`.

## Grid و چگالی

- desktop دو ستون؛ فیلد بلند/notes تمام‌عرض.
- wide form فقط برای جدول یا workflow چندستونه واقعی.
- mobile تک‌ستون؛ ترتیب DOM همان ترتیب منطقی کار.
- input canonical حدود 42px؛ touch target کنترل تعاملی حداقل مناسب.

## Validation و State

- هر POST: `@Html.AntiForgeryToken()` صریح.
- خطا نزدیک فیلد با `.ak-field-error`.
- summary ابتدای فرم برای خطاهای کلی.
- submit در حالت busy قفل و label آن روشن.
- disabled، readonly و hidden semantics موجود حفظ شود.
- input وابسته space رزرو کند تا layout نپرد.

## Amounts و line items

- عدد/مبلغ align انتهایی و LTR/tabular.
- unit/currency کنار مقدار.
- line item table از `.ak-table` و wrapper استفاده کند.
- total preview و review نهایی از داده واقعی input ساخته شود.

## Reuse

- `_CreatePageShell`, `_EditPageShell`
- `_AkPageHeader`, `_AkSectionHead`, `_AkFooterActions`
- `.ak-form*`
- `AkEntityComboboxTagHelper`
- `ak-datepicker.js`, `finance-forms.js`
- `_ValidationScriptsPartial`

## Anti-pattern

- فرم یک‌تکه طولانی
- placeholder به‌جای label
- grid پنج‌ستونه در mobile
- inline JS/CSS تازه وقتی component shared وجود دارد
- تغییر name/id/data hook
- دکمه‌های Save متعدد
- modal برای workflow طولانی و چندمرحله‌ای

