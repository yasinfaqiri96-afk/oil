using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptLossMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LossMode",
                table: "LoadingReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceipts_LossMode",
                table: "LoadingReceipts",
                column: "LossMode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoadingReceipts_LossMode",
                table: "LoadingReceipts");

            migrationBuilder.DropColumn(
                name: "LossMode",
                table: "LoadingReceipts");
        }
    }
}
