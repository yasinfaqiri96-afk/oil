using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalSearchKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Wagons",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Trucks",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Suppliers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Partners",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "LoadingRegisters",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Customers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Contracts",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                table: "Companies",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wagons_SearchKey",
                table: "Wagons",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Trucks_SearchKey",
                table: "Trucks",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_SearchKey",
                table: "Suppliers",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Partners_SearchKey",
                table: "Partners",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingRegisters_SearchKey",
                table: "LoadingRegisters",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_SearchKey",
                table: "Customers",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_SearchKey",
                table: "Contracts",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_SearchKey",
                table: "Companies",
                column: "SearchKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wagons_SearchKey",
                table: "Wagons");

            migrationBuilder.DropIndex(
                name: "IX_Trucks_SearchKey",
                table: "Trucks");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_SearchKey",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Partners_SearchKey",
                table: "Partners");

            migrationBuilder.DropIndex(
                name: "IX_LoadingRegisters_SearchKey",
                table: "LoadingRegisters");

            migrationBuilder.DropIndex(
                name: "IX_Customers_SearchKey",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_SearchKey",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Companies_SearchKey",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Wagons");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Trucks");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SearchKey",
                table: "Companies");
        }
    }
}
