---
name: ptg-release-deployment-guard
description: Use only when the user explicitly asks to publish, deploy, release, restart a production service, replace a production database, synchronize a server, or verify a live PTG deployment.
---

# PTG Release and Deployment Guard

این Skill فقط با درخواست صریح deploy/release فعال شود.

- قبل از تغییر live، وضعیت git، branch/commit، سرویس، دیتابیس، فضای دیسک و تنظیمات محیط را بررسی کن.
- build و تست متناسب را پیش از انتشار اجرا کن.
- از release directory جدا، backup تأییدشده و جابه‌جایی atomic استفاده کن.
- secret و داده production را محافظت کن؛ دیتابیس را بدون اجازه صریح replace نکن.
- بعد از انتشار، service status، health endpoint، login اصلی و logهای تازه را بررسی کن.
- rollback روشن و قابل اجرا نگه دار و نتیجه live را با شواهد گزارش کن.

