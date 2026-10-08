using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportReceiptTruckFreightFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "InventoryTransportReceipts"
                    ADD COLUMN IF NOT EXISTS "AllowanceMt" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "ChargeableShortageMt" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "FreightCostUsd" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "FreightPayableUsd" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "FreightRateUsdPerMt" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "ShortageChargeUsd" numeric(18,4),
                    ADD COLUMN IF NOT EXISTS "ShortageRateUsd" numeric(18,4);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "InventoryTransportReceipts"
                    DROP COLUMN IF EXISTS "AllowanceMt",
                    DROP COLUMN IF EXISTS "ChargeableShortageMt",
                    DROP COLUMN IF EXISTS "FreightCostUsd",
                    DROP COLUMN IF EXISTS "FreightPayableUsd",
                    DROP COLUMN IF EXISTS "FreightRateUsdPerMt",
                    DROP COLUMN IF EXISTS "ShortageChargeUsd",
                    DROP COLUMN IF EXISTS "ShortageRateUsd";
                """);
        }
    }
}
