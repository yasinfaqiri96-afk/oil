using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingRegisterTransportBatchFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LoadingPriceUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogisticsCompanyName",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteDescription",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TransportExpenseUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransportType",
                table: "LoadingRegisters",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoadingPriceUsd",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "LogisticsCompanyName",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RouteDescription",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "TransportExpenseUsd",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "TransportType",
                table: "LoadingRegisters");
        }
    }
}
