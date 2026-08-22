using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentPartnerFundingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CashAccountId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "FundingSource",
                table: "PaymentTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PaidByPartnerId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_PaidByPartnerId",
                table: "PaymentTransactions",
                column: "PaidByPartnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_Partners_PaidByPartnerId",
                table: "PaymentTransactions",
                column: "PaidByPartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_Partners_PaidByPartnerId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_PaidByPartnerId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "FundingSource",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "PaidByPartnerId",
                table: "PaymentTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "CashAccountId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
