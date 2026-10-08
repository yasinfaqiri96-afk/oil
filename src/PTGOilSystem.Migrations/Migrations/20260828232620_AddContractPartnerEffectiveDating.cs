using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <summary>
    /// PTG-P0-03 — سهم شرکای قرارداد تاریخ‌دار می‌شود.
    ///
    /// پیش از این، هر شریک فقط یک سطر «زنده» داشت و همهٔ گزارش‌ها درصدِ امروز را روی رویدادهای
    /// گذشته هم اعمال می‌کردند؛ نتیجه این بود که تغییر ۵۰/۵۰ به ۸۰/۲۰ سهمِ مفادِ دوره‌های بستهٔ
    /// گذشته را هم بازنویسی می‌کرد (اندازه‌گیری‌شده: ۱۶۲٬۰۰۰ USD جابه‌جایی بدون هیچ رویداد مالی).
    ///
    /// این migration:
    ///   • دو ستون <c>EffectiveFrom</c> / <c>EffectiveTo</c> اضافه می‌کند (الگوی موجودِ
    ///     <c>AssetOwnershipShare</c> در همین سیستم).
    ///   • داده‌های موجود را به یک بازهٔ واحد تبدیل می‌کند که از قدیمی‌ترین تاریخِ قابل اثبات
    ///     آغاز می‌شود: <c>LEAST(ContractPartners.CreatedAtUtc, Contracts.ContractDate)</c>.
    ///     هیچ تاریخِ ساختگی اختراع نمی‌شود؛ چون هر قرارداد فقط یک بازه می‌گیرد، همهٔ ارقام
    ///     تاریخی دقیقاً مثل قبل محاسبه می‌شوند.
    ///   • کلید یکتا را از (قرارداد، شریک) به (قرارداد، شریک، آغاز بازه) می‌برد.
    ///   • نگهبان PTG-P0-04 را به‌روز می‌کند تا «جمع = ۱۰۰» را برای هر بازه جداگانه بسنجد،
    ///     نه روی مجموع همهٔ بازه‌ها.
    ///
    /// هیچ ردیفی حذف نمی‌شود و هیچ مبلغی تغییر نمی‌کند.
    ///
    /// محدودیتِ آگاهانه: اگر شرکتی در گذشته سهم را عوض کرده باشد، سیستم هیچ سابقه‌ای از آن
    /// تغییر ندارد و بازسازی‌اش ممکن نیست؛ بنابراین کل گذشته زیر «آخرین ترکیبِ ثبت‌شده» می‌ماند
    /// — همان چیزی که امروز هم گزارش می‌شود. از این پس هر تغییر بازهٔ خودش را می‌سازد.
    /// </summary>
    public partial class AddContractPartnerEffectiveDating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFrom",
                table: "ContractPartners",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveTo",
                table: "ContractPartners",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill قطعی: قدیمی‌ترین تاریخِ قابل اثبات برای همان سطر.
            migrationBuilder.Sql("""
                UPDATE "ContractPartners" cp
                SET "EffectiveFrom" = date_trunc('day', LEAST(cp."CreatedAtUtc", c."ContractDate"))
                FROM "Contracts" c
                WHERE c."Id" = cp."ContractId";
                """);

            // سطرِ یتیم (قرارداد حذف‌شده) نباید 0001-01-01 بماند.
            migrationBuilder.Sql("""
                UPDATE "ContractPartners"
                SET "EffectiveFrom" = date_trunc('day', "CreatedAtUtc")
                WHERE "EffectiveFrom" = TIMESTAMPTZ '0001-01-01 00:00:00+00';
                """);

            // تریگرِ نگهبانِ سهم روی "ContractPartners" از نوع
            // DEFERRABLE INITIALLY DEFERRED است؛ دو UPDATE بالا برای آن رویدادِ معوق
            // صف می‌کنند و PostgreSQL اجازهٔ DDL روی جدولی با رویدادِ معوق را نمی‌دهد
            // (55006: cannot CREATE INDEX ... because it has pending trigger events).
            // با IMMEDIATE کردن، رویدادها همین‌جا resolve می‌شوند و DDL بعدی اجرا می‌شود.
            migrationBuilder.Sql("SET CONSTRAINTS ALL IMMEDIATE;");

            migrationBuilder.DropIndex(
                name: "IX_ContractPartners_ContractId_PartnerId",
                table: "ContractPartners");

            migrationBuilder.CreateIndex(
                name: "IX_ContractPartners_ContractId_EffectiveFrom",
                table: "ContractPartners",
                columns: new[] { "ContractId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractPartners_ContractId_PartnerId_EffectiveFrom",
                table: "ContractPartners",
                columns: new[] { "ContractId", "PartnerId", "EffectiveFrom" },
                unique: true);

            // PTG-P0-04 + PTG-P0-03 — «جمع = ۱۰۰» حالا برای هر بازه جداگانه سنجیده می‌شود.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ptg_check_contract_partner_shares"()
                RETURNS trigger AS $$
                DECLARE
                    affected_contract_id integer;
                    bad_period record;
                BEGIN
                    affected_contract_id := COALESCE(NEW."ContractId", OLD."ContractId");
                    IF affected_contract_id IS NULL THEN
                        RETURN NULL;
                    END IF;

                    -- فقط قرارداد شراکتی (ContractOwnershipType.Partnership = 2).
                    IF NOT EXISTS (
                        SELECT 1 FROM "Contracts" c
                        WHERE c."Id" = affected_contract_id
                          AND c."OwnershipType" = 2)
                    THEN
                        RETURN NULL;
                    END IF;

                    -- هر بازهٔ سهم (هر EffectiveFrom) باید خودش کامل و معتبر باشد.
                    SELECT cp."EffectiveFrom" AS period_start,
                           SUM(cp."SharePercent") AS share_total,
                           COUNT(*) FILTER (
                               WHERE cp."SharePercent" <= 0 OR cp."SharePercent" > 100) AS invalid_rows
                      INTO bad_period
                      FROM "ContractPartners" cp
                     WHERE cp."ContractId" = affected_contract_id
                     GROUP BY cp."EffectiveFrom"
                    HAVING ABS(SUM(cp."SharePercent") - 100) > 0.0001
                        OR COUNT(*) FILTER (
                               WHERE cp."SharePercent" <= 0 OR cp."SharePercent" > 100) > 0
                     LIMIT 1;

                    IF NOT FOUND THEN
                        RETURN NULL;
                    END IF;

                    IF bad_period.invalid_rows > 0 THEN
                        RAISE EXCEPTION
                            'PTG_PARTNER_SHARE_INVALID: contract %, period starting %, every partner share must be greater than 0 and at most 100.',
                            affected_contract_id, bad_period.period_start
                            USING ERRCODE = '23514';
                    END IF;

                    RAISE EXCEPTION
                        'PTG_PARTNER_SHARE_SUM: contract % has partner shares totalling % percent for the period starting %; the total must be exactly 100.',
                        affected_contract_id, bad_period.share_total, bad_period.period_start
                        USING ERRCODE = '23514';
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // بازه‌های تاریخی جمع می‌شوند تا کلید یکتای قدیمی دوباره برقرار شود:
            // فقط جدیدترین بازهٔ هر (قرارداد، شریک) می‌ماند — همان چیزی که مدل قبلی می‌فهمید.
            migrationBuilder.Sql("""
                DELETE FROM "ContractPartners" cp
                USING "ContractPartners" newer
                WHERE cp."ContractId" = newer."ContractId"
                  AND cp."PartnerId" = newer."PartnerId"
                  AND newer."EffectiveFrom" > cp."EffectiveFrom";
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ptg_check_contract_partner_shares"()
                RETURNS trigger AS $$
                DECLARE
                    affected_contract_id integer;
                    share_total numeric(12,4);
                    share_count integer;
                    invalid_rows integer;
                BEGIN
                    affected_contract_id := COALESCE(NEW."ContractId", OLD."ContractId");
                    IF affected_contract_id IS NULL THEN
                        RETURN NULL;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1 FROM "Contracts" c
                        WHERE c."Id" = affected_contract_id
                          AND c."OwnershipType" = 2)
                    THEN
                        RETURN NULL;
                    END IF;

                    SELECT COALESCE(SUM(cp."SharePercent"), 0),
                           COUNT(*),
                           COUNT(*) FILTER (WHERE cp."SharePercent" <= 0 OR cp."SharePercent" > 100)
                      INTO share_total, share_count, invalid_rows
                      FROM "ContractPartners" cp
                     WHERE cp."ContractId" = affected_contract_id;

                    IF share_count = 0 THEN
                        RETURN NULL;
                    END IF;

                    IF invalid_rows > 0 THEN
                        RAISE EXCEPTION
                            'PTG_PARTNER_SHARE_INVALID: contract %, every partner share must be greater than 0 and at most 100.',
                            affected_contract_id
                            USING ERRCODE = '23514';
                    END IF;

                    IF ABS(share_total - 100) > 0.0001 THEN
                        RAISE EXCEPTION
                            'PTG_PARTNER_SHARE_SUM: contract % has partner shares totalling % percent; the total must be exactly 100.',
                            affected_contract_id, share_total
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.DropIndex(
                name: "IX_ContractPartners_ContractId_EffectiveFrom",
                table: "ContractPartners");

            migrationBuilder.DropIndex(
                name: "IX_ContractPartners_ContractId_PartnerId_EffectiveFrom",
                table: "ContractPartners");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "ContractPartners");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "ContractPartners");

            migrationBuilder.CreateIndex(
                name: "IX_ContractPartners_ContractId_PartnerId",
                table: "ContractPartners",
                columns: new[] { "ContractId", "PartnerId" },
                unique: true);
        }
    }
}
