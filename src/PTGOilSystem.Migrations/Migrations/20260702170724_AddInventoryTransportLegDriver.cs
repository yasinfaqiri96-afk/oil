using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportLegDriver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DriverId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_DriverId",
                table: "InventoryTransportLegs",
                column: "DriverId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Drivers_DriverId",
                table: "InventoryTransportLegs",
                column: "DriverId",
                principalTable: "Drivers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Drivers_DriverId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_DriverId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "DriverId",
                table: "InventoryTransportLegs");
        }
    }
}
