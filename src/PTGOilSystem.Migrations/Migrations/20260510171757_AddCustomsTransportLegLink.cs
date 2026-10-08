using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomsTransportLegLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "LoadingRegisterId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "TransportLegId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_TransportLegId",
                table: "CustomsDeclarations",
                column: "TransportLegId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations",
                sql: "(\"LoadingRegisterId\" IS NOT NULL AND \"TransportLegId\" IS NULL) OR (\"LoadingRegisterId\" IS NULL AND \"TransportLegId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomsDeclarations_InventoryTransportLegs_TransportLegId",
                table: "CustomsDeclarations",
                column: "TransportLegId",
                principalTable: "InventoryTransportLegs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomsDeclarations_InventoryTransportLegs_TransportLegId",
                table: "CustomsDeclarations");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_TransportLegId",
                table: "CustomsDeclarations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "TransportLegId",
                table: "CustomsDeclarations");

            migrationBuilder.AlterColumn<int>(
                name: "LoadingRegisterId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
