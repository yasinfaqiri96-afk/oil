using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationDayFxToSupplierPaymentAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AllocatedValueUsdAtAllocation",
                table: "SupplierPaymentAllocations",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeDifferenceLedgerEntryId",
                table: "SupplierPaymentAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeDifferenceType",
                table: "SupplierPaymentAllocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeDifferenceUsd",
                table: "SupplierPaymentAllocations",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PaymentCurrencyFxRateToUsdAtAllocation",
                table: "SupplierPaymentAllocations",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<decimal>(
                name: "PaymentCurrencyPerUsdRateAtAllocation",
                table: "SupplierPaymentAllocations",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 1m);

            // Backfill: existing allocations were valued only at the payment-day rate, so their
            // allocation-day rate IS the payment rate and the exchange difference is exactly zero.
            // This keeps every historic row behaving as it did before this feature.
            migrationBuilder.Sql("""
                UPDATE "SupplierPaymentAllocations"
                SET "PaymentCurrencyFxRateToUsdAtAllocation" =
                        CASE WHEN "PaymentFxRateToUsd" > 0 THEN "PaymentFxRateToUsd" ELSE 1 END,
                    "PaymentCurrencyPerUsdRateAtAllocation" =
                        CASE WHEN "PaymentFxRateToUsd" > 0 THEN ROUND(1 / "PaymentFxRateToUsd", 6) ELSE 1 END,
                    "AllocatedValueUsdAtAllocation" = "AllocatedBookAmountUsd",
                    "ExchangeDifferenceUsd" = 0,
                    "ExchangeDifferenceType" = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_ExchangeDifferenceLedgerEntryId",
                table: "SupplierPaymentAllocations",
                column: "ExchangeDifferenceLedgerEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierPaymentAllocations_LedgerEntries_ExchangeDifference~",
                table: "SupplierPaymentAllocations",
                column: "ExchangeDifferenceLedgerEntryId",
                principalTable: "LedgerEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierPaymentAllocations_LedgerEntries_ExchangeDifference~",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPaymentAllocations_ExchangeDifferenceLedgerEntryId",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "AllocatedValueUsdAtAllocation",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "ExchangeDifferenceLedgerEntryId",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "ExchangeDifferenceType",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "ExchangeDifferenceUsd",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "PaymentCurrencyFxRateToUsdAtAllocation",
                table: "SupplierPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "PaymentCurrencyPerUsdRateAtAllocation",
                table: "SupplierPaymentAllocations");
        }
    }
}
