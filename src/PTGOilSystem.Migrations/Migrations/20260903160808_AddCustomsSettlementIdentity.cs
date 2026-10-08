using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomsSettlementIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomsComponentGroup",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomsDeclarationId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DutyCashAccountId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DutyServiceProviderId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DutySettlementMode",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ServiceCashAccountId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceSettlementMode",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_CustomsDeclarationId_CustomsComponentGr~",
                table: "ExpenseTransactions",
                columns: new[] { "CustomsDeclarationId", "CustomsComponentGroup" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_DutyCashAccountId",
                table: "CustomsDeclarations",
                column: "DutyCashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_DutyServiceProviderId",
                table: "CustomsDeclarations",
                column: "DutyServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_ServiceCashAccountId",
                table: "CustomsDeclarations",
                column: "ServiceCashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_ServiceProviderId",
                table: "CustomsDeclarations",
                column: "ServiceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomsDeclarations_CashAccounts_DutyCashAccountId",
                table: "CustomsDeclarations",
                column: "DutyCashAccountId",
                principalTable: "CashAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomsDeclarations_CashAccounts_ServiceCashAccountId",
                table: "CustomsDeclarations",
                column: "ServiceCashAccountId",
                principalTable: "CashAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomsDeclarations_ServiceProviders_ServiceProviderId",
                table: "CustomsDeclarations",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_CustomsDeclarations_CustomsDeclarationId",
                table: "ExpenseTransactions",
                column: "CustomsDeclarationId",
                principalTable: "CustomsDeclarations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomsDeclarations_CashAccounts_DutyCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomsDeclarations_CashAccounts_ServiceCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomsDeclarations_ServiceProviders_ServiceProviderId",
                table: "CustomsDeclarations");

            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_CustomsDeclarations_CustomsDeclarationId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_CustomsDeclarationId_CustomsComponentGr~",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_DutyCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_DutyServiceProviderId",
                table: "CustomsDeclarations");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_ServiceCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_ServiceProviderId",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "CustomsComponentGroup",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "CustomsDeclarationId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "DutyCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "DutyServiceProviderId",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "DutySettlementMode",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "ServiceCashAccountId",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "ServiceSettlementMode",
                table: "CustomsDeclarations");
        }
    }
}
