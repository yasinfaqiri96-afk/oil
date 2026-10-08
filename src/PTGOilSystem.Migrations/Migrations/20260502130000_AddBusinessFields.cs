using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260502130000_AddBusinessFields")]
    public partial class AddBusinessFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPriceUsd",
                table: "Contracts",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RwbNo",
                table: "LoadingRegisters",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WarehouseExpenseUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtherExpenseUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortageRateUsd",
                table: "TruckDispatches",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FreightPayableUsd",
                table: "TruckDispatches",
                type: "numeric(18,4)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinimumPriceUsd",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "RwbNo",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "WarehouseExpenseUsd",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "OtherExpenseUsd",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "ShortageRateUsd",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "FreightPayableUsd",
                table: "TruckDispatches");
        }
    }
}
