using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddConcurrencyVersionToEditableEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "TruckDispatches",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "SalesTransactions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "LossEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "LoadingRegisters",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "InventoryTransportLegs",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ExpenseTransactions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Contracts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ContractPartners",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "LossEvents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ContractPartners");
        }
    }
}
