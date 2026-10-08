using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class WidenExpenseAndJournalFxRatePrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "JournalEntryLines",
                type: "numeric(24,12)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "AppliedFxRateToUsd",
                table: "ExpenseTransactions",
                type: "numeric(24,12)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "JournalEntryLines",
                type: "numeric(18,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)");

            migrationBuilder.AlterColumn<decimal>(
                name: "AppliedFxRateToUsd",
                table: "ExpenseTransactions",
                type: "numeric(18,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,12)",
                oldNullable: true);
        }
    }
}
