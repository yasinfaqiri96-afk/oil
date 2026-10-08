using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingRegisterTransportOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SourceTerminalId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SourceLoadingRegisterId",
                table: "InventoryTransportLegAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SourceTerminalId",
                table: "InventoryTransportBatches",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourceLoadingRegisterId",
                table: "InventoryTransportLegAllocations",
                column: "SourceLoadingRegisterId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegAllocations_LoadingRegisters_SourceLoa~",
                table: "InventoryTransportLegAllocations",
                column: "SourceLoadingRegisterId",
                principalTable: "LoadingRegisters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegAllocations_LoadingRegisters_SourceLoa~",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegAllocations_SourceLoadingRegisterId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropColumn(
                name: "SourceLoadingRegisterId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.AlterColumn<int>(
                name: "SourceTerminalId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SourceTerminalId",
                table: "InventoryTransportBatches",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
