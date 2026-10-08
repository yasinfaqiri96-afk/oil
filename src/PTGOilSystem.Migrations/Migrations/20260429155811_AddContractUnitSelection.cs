using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddContractUnitSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnitId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_UnitId",
                table: "Contracts",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Units_UnitId",
                table: "Contracts",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Units_UnitId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_UnitId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "Contracts");
        }
    }
}
