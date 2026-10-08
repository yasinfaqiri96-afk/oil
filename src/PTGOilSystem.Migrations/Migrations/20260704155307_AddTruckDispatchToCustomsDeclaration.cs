using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTruckDispatchToCustomsDeclaration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations");

            migrationBuilder.AddColumn<int>(
                name: "TruckDispatchId",
                table: "CustomsDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_TruckDispatchId",
                table: "CustomsDeclarations",
                column: "TruckDispatchId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations",
                sql: "((CASE WHEN \"LoadingRegisterId\" IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN \"TransportLegId\" IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN \"TruckDispatchId\" IS NOT NULL THEN 1 ELSE 0 END)) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomsDeclarations_TruckDispatches_TruckDispatchId",
                table: "CustomsDeclarations",
                column: "TruckDispatchId",
                principalTable: "TruckDispatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomsDeclarations_TruckDispatches_TruckDispatchId",
                table: "CustomsDeclarations");

            migrationBuilder.DropIndex(
                name: "IX_CustomsDeclarations_TruckDispatchId",
                table: "CustomsDeclarations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "TruckDispatchId",
                table: "CustomsDeclarations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomsDeclarations_ExactlyOneSource",
                table: "CustomsDeclarations",
                sql: "(\"LoadingRegisterId\" IS NOT NULL AND \"TransportLegId\" IS NULL) OR (\"LoadingRegisterId\" IS NULL AND \"TransportLegId\" IS NOT NULL)");
        }
    }
}
