# PTG Oil System

## Technology
ASP.NET Core MVC .NET 8, EF Core, PostgreSQL, Razor Views, Bootstrap 5 RTL.

## Mandatory Rules
- Read and understand the existing code before making changes.
- Only modify files directly related to the current request.
- Do not refactor or redesign unrelated areas.
- Do not change Entity, Migration, DbContext, or database structure unless explicitly requested.
- Do not change Stock, Inventory, Ledger, Payment, Sales, FX, or P&L logic unless explicitly requested.
- Never guess business behavior.
- First identify the root cause and related files.
- Make the smallest safe change.
- Run build and relevant tests after changes.
- Keep final responses short and precise.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

## PTG UI/UX Design Rules

1. برای هر درخواست UI/UX ابتدا Skill محلی `.claude/skills/ui-ux-pro-max/SKILL.md` فعال و کامل خوانده شود.
2. پیش از طراحی، `design-system/MASTER.md` خوانده شود.
3. سپس override مربوط به نوع همان صفحه از `design-system/pages/` خوانده شود.
4. حداقل دو صفحه مشابه موجود و componentهای shared آن‌ها به‌عنوان مرجع بررسی شوند.
5. componentهای فعلی سیستم بر ساخت component جدید اولویت دارند.
6. طراحی با ASP.NET Core MVC، Razor و Bootstrap 5 انجام شود.
7. React، Vue، Tailwind، shadcn و framework جدید ممنوع است.
8. Business Logic، Controller، Model، Route، permission و Database بدون ضرورت و درخواست صریح تغییر نکند.
9. تغییر UI نباید رفتار Form، Validation، Modal، Dropdown، SPA، Quick Create یا Submit را بشکند.
10. RTL و فونت Vazirmatn الزامی است؛ اعداد مالی با جهت و tabular مناسب نمایش داده شوند.
11. طراحی تمیز، حرفه‌ای، کنترل‌شده و Enterprise باشد؛ نه نمایشی، کودکانه یا شلوغ.
12. هر صفحه باید با کل سیستم هماهنگ باشد، نه فقط به‌تنهایی زیبا.
13. CSS جدید فقط وقتی ساخته شود که component یا token موجود پاسخ ندهد؛ token و Design System موازی ممنوع است.
14. Inline style، CSS تکراری و `!important` جدید ممنوع است.
15. build و targeted test مرتبط پس از تغییر اجرا شود.
16. تغییر خارج از scope، بازطراحی سراسری و cleanup نامرتبط ممنوع است.
17. پیش از پیاده‌سازی، فایل‌های مرتبط، رفتارهای حفظ‌شونده و plan کوتاه اعلام شود.
18. پس از پیاده‌سازی، فایل‌های تغییرکرده، تست‌ها، browser evidence و محدودیت‌های بررسی گزارش شود.

### Mandatory source order

`business workflow → existing runtime components/tokens → MASTER.md → page override → ui-ux-pro-max suggestions`

پیشنهاد Skill که با RTL، Razor، Bootstrap، Vazirmatn، تراکم ERP یا هویت PTG ناسازگار باشد باید رد یا اصلاح شود.
