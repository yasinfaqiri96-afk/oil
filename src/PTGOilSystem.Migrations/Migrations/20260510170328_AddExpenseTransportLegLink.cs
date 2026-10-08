using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseTransportLegLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransportLegId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_TransportLegId",
                table: "ExpenseTransactions",
                column: "TransportLegId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_InventoryTransportLegs_TransportLegId",
                table: "ExpenseTransactions",
                column: "TransportLegId",
                principalTable: "InventoryTransportLegs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_InventoryTransportLegs_TransportLegId",
                table: "ExpenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_TransportLegId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "TransportLegId",
                table: "ExpenseTransactions");
        }
    }
}
