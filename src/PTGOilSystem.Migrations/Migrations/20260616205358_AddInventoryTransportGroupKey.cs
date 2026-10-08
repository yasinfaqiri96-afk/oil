using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportGroupKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransportGroupKey",
                table: "InventoryTransportLegs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_TransportGroupKey",
                table: "InventoryTransportLegs",
                column: "TransportGroupKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_TransportGroupKey",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "TransportGroupKey",
                table: "InventoryTransportLegs");
        }
    }
}
