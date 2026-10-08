using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryMovementSalesTransactionLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesTransactionId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SalesTransactionId",
                table: "InventoryMovements",
                column: "SalesTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_SalesTransactions_SalesTransactionId",
                table: "InventoryMovements",
                column: "SalesTransactionId",
                principalTable: "SalesTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_SalesTransactions_SalesTransactionId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_SalesTransactionId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "SalesTransactionId",
                table: "InventoryMovements");
        }
    }
}
