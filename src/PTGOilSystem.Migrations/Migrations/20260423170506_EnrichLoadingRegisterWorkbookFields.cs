using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class EnrichLoadingRegisterWorkbookFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsigneeName",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationName",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WagonNumber",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsigneeName",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "DestinationName",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "WagonNumber",
                table: "LoadingRegisters");
        }
    }
}
