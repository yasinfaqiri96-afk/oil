using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingSettlementRubFromFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SettlementUnitPriceRub",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SettlementValueRub",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettlementUnitPriceRub",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "SettlementValueRub",
                table: "LoadingRegisters");
        }
    }
}
