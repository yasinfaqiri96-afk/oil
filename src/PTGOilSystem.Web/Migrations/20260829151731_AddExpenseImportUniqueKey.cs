using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseImportUniqueKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportUniqueKey",
                table: "ExpenseTransactions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_ImportUniqueKey",
                table: "ExpenseTransactions",
                column: "ImportUniqueKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_ImportUniqueKey",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "ImportUniqueKey",
                table: "ExpenseTransactions");
        }
    }
}
