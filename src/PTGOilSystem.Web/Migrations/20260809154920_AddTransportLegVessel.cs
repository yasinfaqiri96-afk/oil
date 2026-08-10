using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTransportLegVessel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VesselId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_VesselId",
                table: "InventoryTransportLegs",
                column: "VesselId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Vessels_VesselId",
                table: "InventoryTransportLegs",
                column: "VesselId",
                principalTable: "Vessels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Vessels_VesselId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_VesselId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "VesselId",
                table: "InventoryTransportLegs");
        }
    }
}
