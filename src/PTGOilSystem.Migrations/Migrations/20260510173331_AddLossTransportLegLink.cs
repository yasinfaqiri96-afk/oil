using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLossTransportLegLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransportLegId",
                table: "LossEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_TransportLegId",
                table: "LossEvents",
                column: "TransportLegId");

            migrationBuilder.AddForeignKey(
                name: "FK_LossEvents_InventoryTransportLegs_TransportLegId",
                table: "LossEvents",
                column: "TransportLegId",
                principalTable: "InventoryTransportLegs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LossEvents_InventoryTransportLegs_TransportLegId",
                table: "LossEvents");

            migrationBuilder.DropIndex(
                name: "IX_LossEvents_TransportLegId",
                table: "LossEvents");

            migrationBuilder.DropColumn(
                name: "TransportLegId",
                table: "LossEvents");
        }
    }
}
