using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSimplePurchaseContractFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DestinationStorageTankId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PurchaseSourceLocationId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_DestinationStorageTankId",
                table: "Contracts",
                column: "DestinationStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_PurchaseSourceLocationId",
                table: "Contracts",
                column: "PurchaseSourceLocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Locations_PurchaseSourceLocationId",
                table: "Contracts",
                column: "PurchaseSourceLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_StorageTanks_DestinationStorageTankId",
                table: "Contracts",
                column: "DestinationStorageTankId",
                principalTable: "StorageTanks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Locations_PurchaseSourceLocationId",
                table: "Contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_StorageTanks_DestinationStorageTankId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_DestinationStorageTankId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_PurchaseSourceLocationId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "DestinationStorageTankId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PurchaseSourceLocationId",
                table: "Contracts");
        }
    }
}
