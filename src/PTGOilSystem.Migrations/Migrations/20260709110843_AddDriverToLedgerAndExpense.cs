using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverToLedgerAndExpense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DriverId",
                table: "LedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DriverId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_DriverId",
                table: "LedgerEntries",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_DriverId",
                table: "ExpenseTransactions",
                column: "DriverId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_Drivers_DriverId",
                table: "ExpenseTransactions",
                column: "DriverId",
                principalTable: "Drivers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_Drivers_DriverId",
                table: "LedgerEntries",
                column: "DriverId",
                principalTable: "Drivers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_Drivers_DriverId",
                table: "ExpenseTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_Drivers_DriverId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_DriverId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_DriverId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "DriverId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "DriverId",
                table: "ExpenseTransactions");
        }
    }
}
