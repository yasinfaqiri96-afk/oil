---
name: ptg-performance-query-audit
description: Use whenever a PTG page, report, query, EF Core operation, dashboard, table, build, or test is reported slow, memory-heavy, database-heavy, or in need of performance optimization.
---

# PTG Performance and Query Audit

قبل از بهینه‌سازی baseline بگیر و bottleneck واقعی را پیدا کن.

- N+1، Includeهای سنگین، client-side filtering، materialization زودهنگام و pagination ناقص را بررسی کن.
- برای read-only از projection و `AsNoTracking` و برای فهرست بزرگ از pagination دیتابیسی استفاده کن.
- index یا cache فقط با دلیل و اندازه‌گیری؛ cache فقط برای lookup پایدار و کوتاه‌مدت.
- منطق محاسبه، ترتیب مالی، stock، ledger و P&L را برای سرعت تغییر نده.
- قبل/بعد را با همان سناریو بسنج و تغییری را که برد واضح ندارد نگه ندار.
- فقط build و تست هدفمند بخش affected را اجرا کن.

