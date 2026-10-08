---
name: ptg-ui-design-rules
description: Use for any PTG UI/UX request — designing or changing a page, form, table, card, modal, chart, or layout. Defines the mandatory source order, the design-system reading order, and the review checklist for PTG Razor/Bootstrap RTL work.
---

# PTG UI/UX Design Rules

## Mandatory source order

برای bug fix کوچک، اصلاح متن یا CSS/JS/View محدود در طراحی موجود، مسیر سبک مقدم است: فقط بخش مربوط و component/token موجود را بخوان؛ خواندن کامل ui-ux-pro-max، MASTER، override و دو صفحهٔ مرجع لازم نیست. ترتیب کامل زیر فقط برای طراحی صفحه/قابلیت جدید یا بازطراحی قابل توجه است. validation را یک بار با `scripts/dev-verify.ps1 -Ui -Paths ...` و تست ساختاری مرتبطِ لازم انجام بده؛ Graphify اجرا نشود. این استثنا بر الزام‌های عمومی بندهای ۱ تا ۴ و ۱۲ مقدم است و قواعد RTL، امنیت و حفظ رفتار همچنان برقرارند.

`business workflow → existing runtime components/tokens → MASTER.md → page override → ui-ux-pro-max suggestions`

پیشنهاد Skill که با RTL، Razor، Bootstrap، IRANSans، تراکم ERP یا هویت PTG ناسازگار باشد باید رد یا اصلاح شود.

## Rules

1. برای هر درخواست UI/UX ابتدا Skill محلی `.claude/skills/ui-ux-pro-max/SKILL.md` فعال و کامل خوانده شود.
2. پیش از طراحی، `design-system/MASTER.md` خوانده شود.
3. سپس override مربوط به نوع همان صفحه از `design-system/pages/` خوانده شود.
4. حداقل دو صفحه مشابه موجود و componentهای shared آن‌ها به‌عنوان مرجع بررسی شوند.
5. componentهای فعلی سیستم بر ساخت component جدید اولویت دارند.
6. طراحی با ASP.NET Core MVC، Razor و Bootstrap 5 انجام شود.
7. تغییر UI نباید رفتار Form، Validation، Modal، Dropdown، SPA، Quick Create یا Submit را بشکند.
8. RTL و فونت IRANSans (با fallback‏ Vazirmatn) الزامی است؛ اعداد مالی با جهت و tabular مناسب نمایش داده شوند.
9. طراحی تمیز، حرفه‌ای، کنترل‌شده و Enterprise باشد؛ نه نمایشی، کودکانه یا شلوغ.
10. هر صفحه باید با کل سیستم هماهنگ باشد، نه فقط به‌تنهایی زیبا.
11. CSS جدید فقط وقتی ساخته شود که component یا token موجود پاسخ ندهد؛ token و Design System موازی ممنوع است.
12. build و targeted test مرتبط پس از تغییر اجرا شود.
13. پیش از پیاده‌سازی، فایل‌های مرتبط، رفتارهای حفظ‌شونده و plan کوتاه اعلام شود.
14. پس از پیاده‌سازی، فایل‌های تغییرکرده، تست‌ها، browser evidence و محدودیت‌های بررسی گزارش شود.
