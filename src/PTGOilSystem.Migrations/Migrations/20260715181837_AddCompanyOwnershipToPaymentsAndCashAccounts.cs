using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyOwnershipToPaymentsAndCashAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "CashAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_CompanyId",
                table: "PaymentTransactions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAccounts_CompanyId",
                table: "CashAccounts",
                column: "CompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashAccounts_Companies_CompanyId",
                table: "CashAccounts",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_Companies_CompanyId",
                table: "PaymentTransactions",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Provable, additive backfill only — ambiguous rows stay NULL and are
            // surfaced by the ownership report. CashAccounts get no backfill because
            // no historical relation proves their company.
            foreach (var statement in PaymentCompanyBackfillSql.Statements)
            {
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashAccounts_Companies_CompanyId",
                table: "CashAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_Companies_CompanyId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_CompanyId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashAccounts_CompanyId",
                table: "CashAccounts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "CashAccounts");
        }
    }
}
