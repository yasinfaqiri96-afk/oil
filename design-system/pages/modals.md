# Modals

## زمان استفاده

Modal فقط برای task کوتاه، context-preserving و قابل تکمیل بدون navigation کامل:

- Quick Create
- ویرایش کوتاه price/expense
- تأیید destructive
- انتخاب یا allocation محدود
- preview/receipt کوتاه

فرم طولانی، report، workflow چندمرحله‌ای یا table بزرگ باید صفحه مستقل باشد.

## ساختار

1. `.modal-content.ak-modal-content` یا `.ak-page-modal`
2. header با `h2` و close
3. body با validation
4. footer با primary + cancel

## Size

- compact برای 1–4 فیلد
- default برای فرم متوسط
- large فقط برای editor/table ضروری
- mobile نزدیک full width با gutter؛ full-screen فقط وقتی usability نیاز دارد

## Behavior

- Bootstrap lifecycle و `modal-design-system.js` حفظ شود.
- focus وارد modal، keyboard trap و Escape طبق Bootstrap.
- close/redirect، Quick Create selection و parent form state حفظ شود.
- submit قفل و busy state نشان داده شود.
- هر POST anti-forgery صریح دارد.
- error modal را بی‌دلیل نبندد.

## Actions

- primary در footer.
- Cancel/Close خنثی.
- destructive قرمز و با confirmation.
- icon-only close دارای aria-label.

## Visual

- radius 8–16px در محدوده موجود.
- shadow فقط dialog token.
- overlay ساده؛ blur/glass شدید ممنوع.
- nested surface و card داخل modal حداقل.

## Reuse

- `_CreateModalShell`, `_ModalLayout`
- `.ak-page-modal`, `.ak-modal-*`
- `.ak-form`, `.ak-field`, `.ak-footer-actions`
- `modal-design-system.js`, Bootstrap modal

## Anti-pattern

- nested modal
- modal برای page-sized workflow
- action در header و footer به‌صورت تکراری
- close بدون حفظ state قراردادشده
- custom focus trap
- animation بلند یا slide نمایشی
- width hard-coded ناسازگار با viewport

