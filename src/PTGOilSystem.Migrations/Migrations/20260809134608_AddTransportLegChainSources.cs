using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTransportLegChainSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SourceInventoryMovementId",
                table: "InventoryTransportLegAllocations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SourceTransportLegId",
                table: "InventoryTransportLegAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTransportReceiptId",
                table: "InventoryTransportLegAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourceTransportLegId",
                table: "InventoryTransportLegAllocations",
                column: "SourceTransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourceTransportReceiptId",
                table: "InventoryTransportLegAllocations",
                column: "SourceTransportReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegAllocations_InventoryTransportLegs_Sou~",
                table: "InventoryTransportLegAllocations",
                column: "SourceTransportLegId",
                principalTable: "InventoryTransportLegs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegAllocations_InventoryTransportReceipts~",
                table: "InventoryTransportLegAllocations",
                column: "SourceTransportReceiptId",
                principalTable: "InventoryTransportReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegAllocations_InventoryTransportLegs_Sou~",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegAllocations_InventoryTransportReceipts~",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegAllocations_SourceTransportLegId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegAllocations_SourceTransportReceiptId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropColumn(
                name: "SourceTransportLegId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.DropColumn(
                name: "SourceTransportReceiptId",
                table: "InventoryTransportLegAllocations");

            migrationBuilder.AlterColumn<int>(
                name: "SourceInventoryMovementId",
                table: "InventoryTransportLegAllocations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
