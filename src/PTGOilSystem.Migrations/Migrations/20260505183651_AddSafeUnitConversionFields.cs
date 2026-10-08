using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSafeUnitConversionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUnitCode",
                table: "Units",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "Units",
                type: "numeric(18,10)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBaseUnit",
                table: "Units",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Units",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitType",
                table: "Units",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryUnitConversionNote",
                table: "Products",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecondaryUnitId",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_SecondaryUnitId",
                table: "Products",
                column: "SecondaryUnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Units_SecondaryUnitId",
                table: "Products",
                column: "SecondaryUnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Units_SecondaryUnitId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_SecondaryUnitId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BaseUnitCode",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "ConversionFactorToBase",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "IsBaseUnit",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "UnitType",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "SecondaryUnitConversionNote",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SecondaryUnitId",
                table: "Products");
        }
    }
}
