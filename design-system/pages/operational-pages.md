# Operational Pages

برای Loading، Shipment، Dispatch، Transport، Inventory و Contract Journey.

## اولویت اطلاعات

1. هویت عملیات/قرارداد/محموله
2. مرحله و status فعلی
3. quantity و unit
4. source → destination
5. remaining/shortage/loss
6. هزینه و سندهای مرتبط
7. trace فنی

## Header و Actions

- next valid action primary.
- edit، expense، receipt، loss و export مطابق مرحله و permission.
- actionهای نامعتبر disabled با علت قابل فهم؛ پنهان‌سازی فقط طبق permission/contract موجود.
- returnUrl و linkهای journey حفظ شوند.

## KPI

- 3–5 KPI برای tab/مرحله فعال.
- loaded، delivered، remaining، shortage و cost نمونه‌های معتبرند.
- KPI tabهای دیگر همزمان نشان داده نشود.

## Grid و records

- summary source/destination دو ستون desktop.
- record table برای trip/receipt/loading؛ card per record نساز.
- lineage با ترتیب زمانی/مرحله‌ای و link به entity واقعی.
- actionهای ردیف در menu یا action column.

## Forms

- workflow چندردیفی wide form مجاز است.
- row validation نزدیک همان row.
- bulk/import progress و summary واقعی.
- fieldهای نوع حمل/منبع/قرارداد data hookهای موجود را حفظ کنند.

## Tabs

- tab بر اساس حوزه عملیات، نه component.
- KPI و toolbar tab-aware.
- content سنگین lazy/partial فقط با lifecycle موجود.
- tab count و status از data واقعی.

## Reuse

- AK Detail v2
- `StatCard`, `_DetailsTabs`
- shipment record components
- loading workbook components
- `.ak-table`, `.ak-list`, `.ak-status`
- `ptg-tabs.js`, `contract-journey-tabs.js`

## Responsive

- زیر 992px summary تک‌ستون.
- tableهای wide در wrapper؛ identity و action قابل دسترسی.
- toolbar wrap و primary action قابل لمس.
- modalهای عملیاتی در mobile padding فشرده ولی fieldها تک‌ستون.

## Anti-pattern

- حذف quantity/source/destination برای خلوت‌شدن
- ساخت movement جعلی یا تغییر stock behavior
- نمایش همه stageها در یک صفحه بدون tabs
- icon زیاد در هر row
- duplication بین Shipment و Contract
- مخلوط‌کردن status عملیاتی با accounting status

