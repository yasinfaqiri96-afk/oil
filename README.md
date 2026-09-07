# PTG Oil System

سیستم مالی-عملیاتی وب برای شرکت تجارت مواد نفتی (Saddiqi Group).
ASP.NET Core MVC (.NET 8) · EF Core 8 · PostgreSQL · Razor + Bootstrap 5 RTL · UI فارسی/دری.

## مستندات مرجع

| سند | محتوا |
|---|---|
| [docs/CURRENT-SYSTEM-STATE.md](docs/CURRENT-SYSTEM-STATE.md) | وضعیت فعلی، ماژول‌های فعال، بدهی فنی، اولویت بعدی |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | لایه‌ها، سرویس‌ها، احراز هویت، ساختار پوشه‌ها |
| [docs/DOMAIN-MODEL.md](docs/DOMAIN-MODEL.md) | موجودیت‌ها، قواعد اجباری دامنه، قراردادهای schema |
| [docs/UI-DESIGN-SYSTEM.md](docs/UI-DESIGN-SYSTEM.md) | Design System `ak-*`: توکن‌ها، کامپوننت‌ها، قواعد |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | اجرای محلی، connection string، bootstrap admin، بکاپ |
| [docs/TESTING.md](docs/TESTING.md) | اجرای تست، baseline، شکست‌های شناخته‌شده |

قواعد کار عامل‌های AI روی این مخزن: [AGENTS.md](AGENTS.md) و [CLAUDE.md](CLAUDE.md).

## جریان کسب‌وکار

```text
Contract → Platt's / FX → Loading → Receipt → Inventory (Ilinka Stock)
        → Transport (موتر / واگن / کشتی) → Dispatch → Sales
        → Expenses → Payments / Ledger → Balance / Statements / P&L
```

## اجرای سریع (Windows)

```powershell
.\scripts\run-local.ps1 -ApplyMigrations
```

برای توسعه با hot reload:

```bat
run-dev.bat
```

برنامه روی `http://localhost:5000` بالا می‌آید. بدون connection string معتبر PostgreSQL، startup عمداً خطا می‌دهد (InMemory fallback وجود ندارد). جزئیات کامل: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## تست

```powershell
dotnet test tests/PTGOilSystem.Web.Tests/PTGOilSystem.Web.Tests.csproj
```

baseline فعلی: `883 pass / 16 fail / 899 total`. ۱۶ شکست بدهی قدیمی و شناخته‌شده‌اند — [docs/TESTING.md](docs/TESTING.md).

## ساختار

```text
src/PTGOilSystem.Web/   پروژهٔ اصلی MVC (Controllers, Services, Entities, Views, wwwroot)
tests/                  xUnit
scripts/                اجرا و نگهداری محلی
tools/db-cleaner/       ابزار پاک‌سازی دیتابیس
db/manual-migrations/   backfillهای SQL دستی
docs/                   مستندات مرجع
```

## قواعد اصلی توسعه

- کد موجود را قبل از تغییر بخوان و بفهم.
- فقط فایل‌های مرتبط با درخواست فعلی را تغییر بده.
- Entity، Migration، DbContext و ساختار دیتابیس را بدون درخواست صریح تغییر نده.
- منطق Stock، Inventory، Ledger، Payment، Sales، FX و P&L را بدون درخواست صریح تغییر نده.
- رفتار تجاری را حدس نزن.
- کوچک‌ترین تغییر ایمن را انجام بده و بعد build و تست مرتبط را اجرا کن.
