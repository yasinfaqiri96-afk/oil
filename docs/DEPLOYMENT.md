# DEPLOYMENT & LOCAL RUN

## پیش‌نیاز

- .NET 8 SDK
- PostgreSQL (نام پیشنهادی دیتابیس: `ptg_oil_system`)
- `dotnet-ef` نسخهٔ 8:
  ```powershell
  dotnet tool update --global dotnet-ef --version 8.0.10
  ```

## Connection string

برنامه به ترتیب این سه ورودی را می‌خواند:

1. `DATABASE_URL`
2. `ConnectionStrings:DefaultConnection`
3. `ConnectionStrings__DefaultConnection`

اگر هیچ‌کدام تنظیم نباشد برنامه عمداً در startup خطا می‌دهد. **InMemory fallback وجود ندارد** — تا کاربر دیتابیس موقت خالی را با PostgreSQL واقعی اشتباه نگیرد.

روی PowerShell محلی `ConnectionStrings__DefaultConnection` را ترجیح بدهید (نیاز به URL-encoding ندارد).

> credential واقعی را در `README`، `appsettings.*.json` یا هر فایل tracked قرار ندهید.

## اجرای محلی (Windows)

روش استاندارد — `scripts/run-local.ps1`:

```powershell
.\scripts\run-local.ps1 -ApplyMigrations
```

پارامترها: `-Database` · `-DbHost` · `-DbPort` · `-DbUsername` · `-DbPassword` · `-ApplyMigrations` · `-MigrateOnly` · `-Watch`

- اگر `PTG_LOCAL_DB_PASSWORD` در env نباشد، اسکریپت یک‌بار interactive می‌پرسد و به‌صورت per-user در `%LOCALAPPDATA%\PTGOilSystem\local-run-secrets.json` (رمزنگاری‌شده با DPAPI) cache می‌کند.
- فقط migration بدون بالا آوردن سرور: `-MigrateOnly`.

### حالت توسعه (hot reload)

```bat
run-dev.bat
```

wrapper روی `scripts/run-local.ps1 -Watch` (یعنی `dotnet watch`). تغییرات `.cshtml` / CSS / C# خودکار reload می‌شوند. اگر پورت ۵۰۰۰ اشغال باشد اجرا نمی‌شود.

> بدون `run-dev.bat` (اجرای exe ساخته‌شده)، ویرایش‌های `.cshtml`/CSS دیده نمی‌شوند.

## اجرای دستی

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Username=postgres;Password=<PASSWORD>;Database=ptg_oil_system;SSL Mode=Prefer;Trust Server Certificate=true"

dotnet ef database update --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj
dotnet run                --project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj
```

پیش‌فرض روی `http://localhost:5000`.

## Bootstrap admin

در `Program.cs` + `Security/AuthBootstrapper.cs`:

- نقش‌های پایه در startup idempotent seed می‌شوند.
- admin اولیه **فقط** وقتی ساخته می‌شود که هیچ کاربری در دیتابیس نباشد.
- username پیش‌فرض `admin`، fullname پیش‌فرض `System Administrator`.
- اگر `PTG_BOOTSTRAP_ADMIN_PASSWORD` تنظیم باشد همان استفاده می‌شود؛ وگرنه یک رمز یک‌بارمصرف تصادفی تولید و فقط در log startup چاپ می‌شود.

متغیرها: `PTG_BOOTSTRAP_ADMIN_USERNAME` · `PTG_BOOTSTRAP_ADMIN_FULLNAME` · `PTG_BOOTSTRAP_ADMIN_PASSWORD`

## Publish

```powershell
dotnet publish src/PTGOilSystem.Web/PTGOilSystem.Web.csproj -c Release
```

خروجی publish و آرشیوهای انتشار build artifact هستند و نباید commit شوند (`.gitignore` پوشهٔ `artifacts/` را نادیده می‌گیرد).

## پشتیبان‌گیری دیتابیس

- dumpهای PostgreSQL در `backups/postgres/` (خارج از Git).
- `scripts/DecryptDatabaseBackup.ps1` برای بازکردن بکاپ رمزنگاری‌شده.
- اسکریپت‌های پاک‌سازی: `scripts/clear-db-except-users.sql`، `scripts/truncate-except-users.sql` (نسخهٔ dry-run هم موجود است) و ابزار `tools/db-cleaner/`.

> این اسکریپت‌ها داده حذف می‌کنند. قبل از اجرا حتماً dump بگیرید.
