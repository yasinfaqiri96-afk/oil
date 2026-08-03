# Financial Pages

این override برای Payments، Ledger، Statements، Settlements، Accounts و گزارش‌های مالی است.

## اولویت اطلاعات

1. طرف حساب / حساب
2. مبلغ و ارز
3. جهت تراکنش
4. تاریخ و reference
5. مانده، allocation و status
6. سند/trace

Semantics ماژول authoritative است. UI حق معکوس‌کردن Debit/Credit، incoming/outgoing یا sign را ندارد.

## Header و Actions

- primary action فقط ثبت/تأیید مرحله جاری.
- reverse/cancel/destructive در kebab و با confirmation.
- export/print secondary.
- وضعیت ثبت دفتر/تطبیق کنار عنوان یا summary.

## KPI

- 3–4 KPI؛ balance، debit/credit، paid/received، outstanding.
- currency mixing ممنوع؛ FX rate و base amount جدا.
- رنگ فقط برای وضعیت یا variance مهم.

## Forms

- mode switch موجود مثل `ptg-tabs-rail` حفظ شود.
- cash/bank/sarraf/contract allocation با progressive disclosure.
- summary اثر مالی پیش از submit برای workflow پیچیده.
- account، company، fiscal year و date validation پنهان نشود.

## Tables و statements

- ترتیب زمانی و running balance حفظ شود.
- opening balance از transaction rows متمایز.
- ستون‌های amount، currency و balance LTR/tabular.
- reference و ledger link برای auditability باقی بماند.
- هشدار operational را با formal ledger total ادغام نکن.

## Reuse

- finance workspace موجود
- `_PartyStatementTable`, `_SupplierStatementLedger`
- `_ReferenceMetricCard`
- `.ak-summary`, `.ak-table`, `.ak-num`
- `_ExportMenu`, `finance-forms.js`

## Responsive

- رقم و unit کنار هم بمانند.
- ستون audit می‌تواند در mobile به details منتقل شود، ولی از DOM/داده حذف نشود.
- primary action در viewport کوچک قابل دسترسی بماند.

## Anti-pattern

- رنگ سبز برای هر مبلغ مثبت
- مخفی‌کردن currency یا FX
- جمع‌زدن ledger و operational flow بدون reconciliation
- تبدیل statement به cardهای متعدد
- تغییر محاسبه برای هماهنگ‌کردن ظاهر
- متن مبهم «بدهکار/بستانکار» بدون semantics ماژول

