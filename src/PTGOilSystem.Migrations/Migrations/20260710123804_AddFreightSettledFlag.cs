using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFreightSettledFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FreightSettledDate",
                table: "TruckDispatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFreightSettled",
                table: "TruckDispatches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FreightSettledDate",
                table: "InventoryTransportLegs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFreightSettled",
                table: "InventoryTransportLegs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TruckDispatches_IsFreightSettled",
                table: "TruckDispatches",
                column: "IsFreightSettled");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_IsFreightSettled",
                table: "InventoryTransportLegs",
                column: "IsFreightSettled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TruckDispatches_IsFreightSettled",
                table: "TruckDispatches");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_IsFreightSettled",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "FreightSettledDate",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "IsFreightSettled",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "FreightSettledDate",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "IsFreightSettled",
                table: "InventoryTransportLegs");
        }
    }
}
