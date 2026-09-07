using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerReceiptCashApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CustomerPaymentAllocationId",
                table: "CustomerPaymentAllocationApplications",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "PaymentTransactionId",
                table: "CustomerPaymentAllocationApplications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: every existing application row came from a pre-sale allocation, so its
            // receipt is unambiguous. This must run before the FK is added or the rows would
            // point at payment 0.
            migrationBuilder.Sql(@"
                UPDATE ""CustomerPaymentAllocationApplications"" AS app
                SET ""PaymentTransactionId"" = alloc.""PaymentTransactionId""
                FROM ""CustomerPaymentAllocations"" AS alloc
                WHERE app.""CustomerPaymentAllocationId"" = alloc.""Id"";");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocationApplications_PaymentTransactionId_~",
                table: "CustomerPaymentAllocationApplications",
                columns: new[] { "PaymentTransactionId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPaymentAllocationApplications_PaymentTransactions_P~",
                table: "CustomerPaymentAllocationApplications",
                column: "PaymentTransactionId",
                principalTable: "PaymentTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPaymentAllocationApplications_PaymentTransactions_P~",
                table: "CustomerPaymentAllocationApplications");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPaymentAllocationApplications_PaymentTransactionId_~",
                table: "CustomerPaymentAllocationApplications");

            migrationBuilder.DropColumn(
                name: "PaymentTransactionId",
                table: "CustomerPaymentAllocationApplications");

            migrationBuilder.AlterColumn<int>(
                name: "CustomerPaymentAllocationId",
                table: "CustomerPaymentAllocationApplications",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
