using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesTransactionSourcePurchaseContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourcePurchaseContractId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_SourcePurchaseContractId",
                table: "SalesTransactions",
                column: "SourcePurchaseContractId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_Contracts_SourcePurchaseContractId",
                table: "SalesTransactions",
                column: "SourcePurchaseContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_Contracts_SourcePurchaseContractId",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_SourcePurchaseContractId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SourcePurchaseContractId",
                table: "SalesTransactions");
        }
    }
}
