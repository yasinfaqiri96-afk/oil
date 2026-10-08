using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeWaySettlementPerLegCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerPaidCurrency",
                table: "ThreeWaySettlements",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CustomerPaidFxRateToUsd",
                table: "ThreeWaySettlements",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierAcceptedCurrency",
                table: "ThreeWaySettlements",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SupplierAcceptedFxRateToUsd",
                table: "ThreeWaySettlements",
                type: "numeric(18,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerPaidCurrency",
                table: "ThreeWaySettlements");

            migrationBuilder.DropColumn(
                name: "CustomerPaidFxRateToUsd",
                table: "ThreeWaySettlements");

            migrationBuilder.DropColumn(
                name: "SupplierAcceptedCurrency",
                table: "ThreeWaySettlements");

            migrationBuilder.DropColumn(
                name: "SupplierAcceptedFxRateToUsd",
                table: "ThreeWaySettlements");
        }
    }
}
