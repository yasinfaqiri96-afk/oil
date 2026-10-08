using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportReceiptDispatchLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryTransportReceiptId",
                table: "TruckDispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TruckDispatches_InventoryTransportReceiptId",
                table: "TruckDispatches",
                column: "InventoryTransportReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_TruckDispatches_InventoryTransportReceipts_InventoryTranspo~",
                table: "TruckDispatches",
                column: "InventoryTransportReceiptId",
                principalTable: "InventoryTransportReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TruckDispatches_InventoryTransportReceipts_InventoryTranspo~",
                table: "TruckDispatches");

            migrationBuilder.DropIndex(
                name: "IX_TruckDispatches_InventoryTransportReceiptId",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "InventoryTransportReceiptId",
                table: "TruckDispatches");
        }
    }
}
