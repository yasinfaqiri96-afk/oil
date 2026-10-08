using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <summary>
    /// PTG-P0-04 — «جمع درصد سهم شرکای یک قرارداد شراکتی باید دقیقاً ۱۰۰ باشد» تا امروز فقط در
    /// <c>ContractsController.ValidatePartnerShares</c> کنترل می‌شد. هر مسیر دیگری (ایمپورت،
    /// ابزار، اسکریپت، سرویس آینده) می‌توانست سهم‌های ناسازگار بنویسد و صورت‌حساب شراکت بیش از
    /// مفاد واقعی توزیع کند (نمونهٔ اندازه‌گیری‌شده: 160٪ ⇒ 324,000 USD توزیع اضافی).
    ///
    /// این migration همان قاعده را به لایهٔ دیتابیس می‌برد. یک CHECK سطری نمی‌تواند SUM چندسطری را
    /// بسنجد، بنابراین از یک CONSTRAINT TRIGGER با حالت DEFERRABLE INITIALLY DEFERRED استفاده
    /// می‌شود: بررسی در لحظهٔ COMMIT انجام می‌گیرد، پس الگوی رایجِ «حذف همهٔ سهم‌ها و نوشتن دوباره»
    /// در یک تراکنش (کاری که ویرایش قرارداد می‌کند) کاملاً سالم می‌ماند.
    ///
    /// این migration هیچ داده‌ای را تغییر یا حذف نمی‌کند. رکوردهای تاریخیِ ناسازگار (اگر باشند)
    /// دست‌نخورده می‌مانند و فقط هنگام ویرایشِ بعدیِ همان قرارداد اصلاح لازم می‌شود؛ فهرست آن‌ها با
    /// کوئری تطبیق در ReconciliationController/تست قابل استخراج است.
    /// </summary>
    public partial class AddContractPartnerShareSumGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

                    -- فقط قرارداد شراکتی (ContractOwnershipType.Partnership = 2). اگر قرارداد در
                    -- همین تراکنش حذف شده یا به «شخصی» تبدیل شده باشد، چیزی برای بررسی نیست.
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

                    -- همهٔ سطرها حذف شده‌اند (مثلاً حذف قرارداد یا تبدیل نوع مالکیت در همین تراکنش).
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

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_ContractPartners_ShareSum" ON "ContractPartners";
                """);

            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER "TR_ContractPartners_ShareSum"
                AFTER INSERT OR UPDATE OR DELETE ON "ContractPartners"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION "ptg_check_contract_partner_shares"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_ContractPartners_ShareSum" ON "ContractPartners";
                """);

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS "ptg_check_contract_partner_shares"();
                """);
        }
    }
}
