using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectCurrencyPerUsdRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "HistoricalFxRateToUsd",
                table: "SupplierBalanceTransferSources",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AddColumn<decimal>(
                name: "HistoricalCurrencyPerUsdRate",
                table: "SupplierBalanceTransferSources",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "TransferPerUsdRate",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TransferFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "HistoricalFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ContractCurrencyPerUsdRate",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ContractCurrencyFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AddColumn<decimal>(
                name: "HistoricalCurrencyPerUsdRate",
                table: "SupplierBalanceTransfers",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HistoricalRateIsEstimated",
                table: "SupplierBalanceTransfers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<decimal>(
                name: "AppliedFxRateToUsd",
                table: "LedgerEntries",
                type: "numeric(24,12)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AppliedCurrencyPerUsdRate",
                table: "LedgerEntries",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Rate",
                table: "DailyFxRates",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoricalCurrencyPerUsdRate",
                table: "SupplierBalanceTransferSources");

            migrationBuilder.DropColumn(
                name: "HistoricalCurrencyPerUsdRate",
                table: "SupplierBalanceTransfers");

            migrationBuilder.DropColumn(
                name: "HistoricalRateIsEstimated",
                table: "SupplierBalanceTransfers");

            migrationBuilder.DropColumn(
                name: "AppliedCurrencyPerUsdRate",
                table: "LedgerEntries");

            migrationBuilder.AlterColumn<decimal>(
                name: "HistoricalFxRateToUsd",
                table: "SupplierBalanceTransferSources",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TransferPerUsdRate",
                table: "SupplierBalanceTransfers",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TransferFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "HistoricalFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ContractCurrencyPerUsdRate",
                table: "SupplierBalanceTransfers",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ContractCurrencyFxRateToUsd",
                table: "SupplierBalanceTransfers",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "AppliedFxRateToUsd",
                table: "LedgerEntries",
                type: "numeric(18,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Rate",
                table: "DailyFxRates",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");
        }
    }
}
