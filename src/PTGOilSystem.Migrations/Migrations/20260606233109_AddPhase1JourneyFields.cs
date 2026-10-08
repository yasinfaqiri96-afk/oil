using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase1JourneyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DifferenceReason",
                table: "SarrafSettlements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdvancePayment",
                table: "PaymentTransactions",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FreightCostResponsibility",
                table: "LoadingRegisters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostResponsibility",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DifferenceReason",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "IsAdvancePayment",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "FreightCostResponsibility",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "CostResponsibility",
                table: "ExpenseTransactions");
        }
    }
}
