using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesTransactionTruckDispatchLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TruckDispatchId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_TruckDispatchId",
                table: "SalesTransactions",
                column: "TruckDispatchId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_TruckDispatches_TruckDispatchId",
                table: "SalesTransactions",
                column: "TruckDispatchId",
                principalTable: "TruckDispatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_TruckDispatches_TruckDispatchId",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_TruckDispatchId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "TruckDispatchId",
                table: "SalesTransactions");
        }
    }
}
