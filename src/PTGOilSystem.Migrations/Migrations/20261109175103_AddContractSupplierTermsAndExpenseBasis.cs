using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSupplierTermsAndExpenseBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BasisQuantityMt",
                table: "ExpenseTransactions",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CalculationMethod",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RatePerMt",
                table: "ExpenseTransactions",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuantityMt",
                table: "ContractSuppliers",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceUsd",
                table: "ContractSuppliers",
                type: "numeric(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BasisQuantityMt",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "CalculationMethod",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "RatePerMt",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "QuantityMt",
                table: "ContractSuppliers");

            migrationBuilder.DropColumn(
                name: "UnitPriceUsd",
                table: "ContractSuppliers");
        }
    }
}
