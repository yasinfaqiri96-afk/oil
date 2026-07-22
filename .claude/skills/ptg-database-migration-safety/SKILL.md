---
name: ptg-database-migration-safety
description: Use whenever PTG work may change an Entity, DbContext, EF Core model, PostgreSQL schema, migration, index, constraint, relationship, column type, precision, seed data, or production database.
---

# PTG Database and Migration Safety

تغییر دیتابیس، Entity، DbContext یا Migration بدون اجازه واضح کاربر ممنوع است.

- ابتدا migrationهای موجود، model snapshot، روابط و داده‌های فعلی را بررسی کن.
- سازگاری رکوردهای قبلی، nullability، default، precision، foreign key، cascade و lock احتمالی را تحلیل کن.
- SQL تولیدشده را برای data loss و تغییرات ناخواسته مرور کن.
- دیتابیس production را بدون اجازه، backup تأییدشده و طرح rollback تغییر یا جایگزین نکن.
- پس از تغییر واقعی مدل: solution build، تست مرتبط و pending-model check لازم است.
- هر migration باید کوچک، قابل توضیح و قابل برگشت باشد.

