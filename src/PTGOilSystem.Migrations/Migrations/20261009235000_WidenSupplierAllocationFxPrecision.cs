using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace PTGOilSystem.Migrations.Migrations;

public partial class WidenSupplierAllocationFxPrecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<decimal>(name: "ContractCurrencyPerUsdRate", table: "SupplierPaymentAllocations",
            type: "numeric(24,12)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(18,6)");
        migrationBuilder.AlterColumn<decimal>(name: "ContractCurrencyFxRateToUsd", table: "SupplierPaymentAllocations",
            type: "numeric(24,12)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(18,6)");
        migrationBuilder.AlterColumn<decimal>(name: "PaymentCurrencyPerUsdRateAtAllocation", table: "SupplierPaymentAllocations",
            type: "numeric(24,12)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(18,6)");
        migrationBuilder.AlterColumn<decimal>(name: "PaymentCurrencyFxRateToUsdAtAllocation", table: "SupplierPaymentAllocations",
            type: "numeric(24,12)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(18,6)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rollback must never silently round the newly locked historical rates.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "SupplierPaymentAllocations" WHERE
                    "ContractCurrencyPerUsdRate" <> round("ContractCurrencyPerUsdRate", 6) OR abs("ContractCurrencyPerUsdRate") >= 1000000000000 OR
                    "ContractCurrencyFxRateToUsd" <> round("ContractCurrencyFxRateToUsd", 6) OR abs("ContractCurrencyFxRateToUsd") >= 1000000000000 OR
                    "PaymentCurrencyPerUsdRateAtAllocation" <> round("PaymentCurrencyPerUsdRateAtAllocation", 6) OR abs("PaymentCurrencyPerUsdRateAtAllocation") >= 1000000000000 OR
                    "PaymentCurrencyFxRateToUsdAtAllocation" <> round("PaymentCurrencyFxRateToUsdAtAllocation", 6) OR abs("PaymentCurrencyFxRateToUsdAtAllocation") >= 1000000000000 ) THEN
                    RAISE EXCEPTION 'Cannot reduce supplier allocation FX precision without losing historical rates';
                END IF;
            END $$;
            """);
        migrationBuilder.AlterColumn<decimal>(name: "ContractCurrencyPerUsdRate", table: "SupplierPaymentAllocations",
            type: "numeric(18,6)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(24,12)");
        migrationBuilder.AlterColumn<decimal>(name: "ContractCurrencyFxRateToUsd", table: "SupplierPaymentAllocations",
            type: "numeric(18,6)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(24,12)");
        migrationBuilder.AlterColumn<decimal>(name: "PaymentCurrencyPerUsdRateAtAllocation", table: "SupplierPaymentAllocations",
            type: "numeric(18,6)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(24,12)");
        migrationBuilder.AlterColumn<decimal>(name: "PaymentCurrencyFxRateToUsdAtAllocation", table: "SupplierPaymentAllocations",
            type: "numeric(18,6)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(24,12)");
    }
}
