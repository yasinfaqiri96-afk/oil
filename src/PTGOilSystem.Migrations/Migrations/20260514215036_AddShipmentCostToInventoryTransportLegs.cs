using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentCostToInventoryTransportLegs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PurchaseUnitCostUsd",
                table: "InventoryTransportLegs",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShipmentId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_ShipmentId",
                table: "InventoryTransportLegs",
                column: "ShipmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Shipments_ShipmentId",
                table: "InventoryTransportLegs",
                column: "ShipmentId",
                principalTable: "Shipments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Shipments_ShipmentId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_ShipmentId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "PurchaseUnitCostUsd",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "ShipmentId",
                table: "InventoryTransportLegs");
        }
    }
}
