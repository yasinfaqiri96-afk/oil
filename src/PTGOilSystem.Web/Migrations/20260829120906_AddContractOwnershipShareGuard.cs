using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <summary>
    /// PTG ۱۲-E — بستنِ راهِ ورودی‌ای که نگهبانِ سهم را دور می‌زد.
    ///
    /// نگهبانِ PTG-P0-04 روی <c>ContractPartners</c> بسته شده بود، پس اگر قراردادی
    /// مستقیماً از «شخصی» به «شراکتی» تبدیل می‌شد <b>بدون آنکه هیچ سطر شریکی لمس شود</b>،
    /// تریگر اصلاً شلیک نمی‌کرد و قراردادِ شراکتیِ بدون سهمِ معتبر Commit می‌شد.
    ///
    /// این migration یک CONSTRAINT TRIGGER تعویق‌دار روی خودِ <c>Contracts</c> اضافه می‌کند:
    ///
    ///   • هر قرارداد شراکتی که سطر سهم دارد، باید در هر بازه دقیقاً ۱۰۰٪ باشد — مستقل از
    ///     اینکه تغییر از سمت قرارداد آمده یا از سمت شرکا.
    ///   • <b>تبدیلِ</b> شخصی → شراکتی (یعنی UPDATE) بدون هیچ سطر سهم رد می‌شود.
    ///
    /// چرا INSERT از قاعدهٔ دوم مستثناست: ایمپورت‌هایی وجود دارند که قرارداد را در یک
    /// تراکنش و شرکا را در تراکنش بعدی می‌نویسند. اجبارِ «سهم در همان تراکنشِ INSERT»
    /// آن جریان‌ها را می‌شکست. آن حالت با اسکنر <c>PARTNERSHIP-WITHOUT-SHARES</c> در
    /// <c>LedgerIntegrityReconciliationService</c> پیدا می‌شود و در Controller هم
    /// اعتبارسنجی می‌گردد.
    ///
    /// تریگر تعویق‌دار است، پس الگوی «قرارداد را عوض کن و سهم‌ها را در همان تراکنش بنویس»
    /// دست‌نخورده کار می‌کند. هیچ داده‌ای تغییر یا حذف نمی‌شود.
    /// </summary>
    public partial class AddContractOwnershipShareGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ptg_check_contract_ownership_shares"()
                RETURNS trigger AS $$
                DECLARE
                    share_count integer;
                    bad_period record;
                BEGIN
                    -- فقط قرارداد شراکتی (ContractOwnershipType.Partnership = 2).
                    IF NEW."OwnershipType" IS DISTINCT FROM 2 THEN
                        RETURN NULL;
                    END IF;

                    SELECT COUNT(*) INTO share_count
                      FROM "ContractPartners" cp
                     WHERE cp."ContractId" = NEW."Id";

                    IF share_count = 0 THEN
                        -- تبدیلِ یک قرارداد موجود به شراکتی بدون هیچ سهمی: همان شکافِ ۱۲-E.
                        IF TG_OP = 'UPDATE' AND OLD."OwnershipType" IS DISTINCT FROM 2 THEN
                            RAISE EXCEPTION
                                'PTG_PARTNERSHIP_WITHOUT_SHARES: contract % was changed to partnership but has no partner share rows.',
                                NEW."Id"
                                USING ERRCODE = '23514';
                        END IF;

                        -- INSERT دو-مرحله‌ای (قرارداد اکنون، شرکا در تراکنش بعدی) عمداً آزاد است.
                        RETURN NULL;
                    END IF;

                    -- هر بازهٔ سهم باید خودش کامل و معتبر باشد.
                    SELECT cp."EffectiveFrom" AS period_start,
                           SUM(cp."SharePercent") AS share_total,
                           COUNT(*) FILTER (
                               WHERE cp."SharePercent" <= 0 OR cp."SharePercent" > 100) AS invalid_rows
                      INTO bad_period
                      FROM "ContractPartners" cp
                     WHERE cp."ContractId" = NEW."Id"
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
                            NEW."Id", bad_period.period_start
                            USING ERRCODE = '23514';
                    END IF;

                    RAISE EXCEPTION
                        'PTG_PARTNER_SHARE_SUM: contract % has partner shares totalling % percent for the period starting %; the total must be exactly 100.',
                        NEW."Id", bad_period.share_total, bad_period.period_start
                        USING ERRCODE = '23514';
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Contracts_OwnershipShares" ON "Contracts";
                """);

            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER "TR_Contracts_OwnershipShares"
                AFTER INSERT OR UPDATE ON "Contracts"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION "ptg_check_contract_ownership_shares"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Contracts_OwnershipShares" ON "Contracts";
                """);
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS "ptg_check_contract_ownership_shares"();
                """);
        }
    }
}
