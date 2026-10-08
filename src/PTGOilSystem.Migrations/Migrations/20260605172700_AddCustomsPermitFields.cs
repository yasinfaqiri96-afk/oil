using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomsPermitFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomsType",
                table: "CustomsDeclarations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoodsName",
                table: "CustomsDeclarations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermitHolderName",
                table: "CustomsDeclarations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermitNumber",
                table: "CustomsDeclarations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Route",
                table: "CustomsDeclarations",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomsType",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "GoodsName",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "PermitHolderName",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "PermitNumber",
                table: "CustomsDeclarations");

            migrationBuilder.DropColumn(
                name: "Route",
                table: "CustomsDeclarations");
        }
    }
}
