using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyOwnerPartner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerPartnerId",
                table: "Companies",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_OwnerPartnerId",
                table: "Companies",
                column: "OwnerPartnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_Partners_OwnerPartnerId",
                table: "Companies",
                column: "OwnerPartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_Partners_OwnerPartnerId",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Companies_OwnerPartnerId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "OwnerPartnerId",
                table: "Companies");
        }
    }
}
