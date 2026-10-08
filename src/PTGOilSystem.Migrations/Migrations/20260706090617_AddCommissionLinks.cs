using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RelatedExpenseTransactionId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedPaymentTransactionId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelatedExpenseTransactionId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "RelatedPaymentTransactionId",
                table: "ExpenseTransactions");
        }
    }
}
