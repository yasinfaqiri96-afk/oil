---
name: ptg-permission-security-audit
description: Use whenever PTG work touches roles, permissions, authorization, login, admin access, sensitive routes, company or fiscal-year boundaries, user management, direct URLs, API access, or security review.
---

# PTG Permission and Security Audit

امنیت را فقط از روی مخفی بودن دکمه بررسی نکن؛ کنترل اصلی باید server-side باشد.

- Controller/action، policy/role، direct URL و API را بررسی کن.
- anti-forgery، IDOR، privilege escalation و جداسازی company/fiscal year را کنترل کن.
- دسترسی حساس را بدون اجازه باز نکن و اصل least privilege را حفظ کن.
- secret، password، token یا connection string را ثبت یا نمایش نده.
- هم حالت مجاز و هم حالت غیرمجاز را با تست هدفمند بررسی کن.
- قبل از تغییر permission، بخش‌ها و نقش‌های affected را کوتاه اعلام کن.

