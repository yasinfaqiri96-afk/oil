using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTruckDispatchDirectReceiptInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DispatchMode",
                table: "TruckDispatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LoadingReceiptAllocationId",
                table: "TruckDispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TruckDispatches_LoadingReceiptAllocationId",
                table: "TruckDispatches",
                column: "LoadingReceiptAllocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_TruckDispatches_LoadingReceiptAllocations_LoadingReceiptAll~",
                table: "TruckDispatches",
                column: "LoadingReceiptAllocationId",
                principalTable: "LoadingReceiptAllocations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TruckDispatches_LoadingReceiptAllocations_LoadingReceiptAll~",
                table: "TruckDispatches");

            migrationBuilder.DropIndex(
                name: "IX_TruckDispatches_LoadingReceiptAllocationId",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "DispatchMode",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "LoadingReceiptAllocationId",
                table: "TruckDispatches");
        }
    }
}
