using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingAssetRentLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoadingRegisterId",
                table: "AssetRentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_LoadingRegisterId",
                table: "AssetRentTransactions",
                column: "LoadingRegisterId");

            migrationBuilder.AddForeignKey(
                name: "FK_AssetRentTransactions_LoadingRegisters_LoadingRegisterId",
                table: "AssetRentTransactions",
                column: "LoadingRegisterId",
                principalTable: "LoadingRegisters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssetRentTransactions_LoadingRegisters_LoadingRegisterId",
                table: "AssetRentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AssetRentTransactions_LoadingRegisterId",
                table: "AssetRentTransactions");

            migrationBuilder.DropColumn(
                name: "LoadingRegisterId",
                table: "AssetRentTransactions");
        }
    }
}
