using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseLoadingRegisterLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoadingRegisterId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_LoadingRegisterId",
                table: "ExpenseTransactions",
                column: "LoadingRegisterId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_LoadingRegisters_LoadingRegisterId",
                table: "ExpenseTransactions",
                column: "LoadingRegisterId",
                principalTable: "LoadingRegisters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_LoadingRegisters_LoadingRegisterId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_LoadingRegisterId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "LoadingRegisterId",
                table: "ExpenseTransactions");
        }
    }
}
