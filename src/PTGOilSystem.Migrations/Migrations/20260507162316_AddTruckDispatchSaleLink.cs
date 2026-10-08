using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTruckDispatchSaleLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesTransactionId",
                table: "TruckDispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TruckDispatches_SalesTransactionId",
                table: "TruckDispatches",
                column: "SalesTransactionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TruckDispatches_SalesTransactions_SalesTransactionId",
                table: "TruckDispatches",
                column: "SalesTransactionId",
                principalTable: "SalesTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TruckDispatches_SalesTransactions_SalesTransactionId",
                table: "TruckDispatches");

            migrationBuilder.DropIndex(
                name: "IX_TruckDispatches_SalesTransactionId",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "SalesTransactionId",
                table: "TruckDispatches");
        }
    }
}
