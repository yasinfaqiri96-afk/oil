using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseSettlementIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CashAccountId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CounterpartyId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CounterpartyType",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SettlementMode",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_CashAccountId",
                table: "ExpenseTransactions",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_CounterpartyType_CounterpartyId",
                table: "ExpenseTransactions",
                columns: new[] { "CounterpartyType", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_SettlementMode",
                table: "ExpenseTransactions",
                column: "SettlementMode");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_CashAccounts_CashAccountId",
                table: "ExpenseTransactions",
                column: "CashAccountId",
                principalTable: "CashAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_CashAccounts_CashAccountId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_CashAccountId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_CounterpartyType_CounterpartyId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_SettlementMode",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "CashAccountId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "CounterpartyId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "CounterpartyType",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "SettlementMode",
                table: "ExpenseTransactions");
        }
    }
}
