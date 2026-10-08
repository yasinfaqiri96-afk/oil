using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerCurrentAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartnerCurrentAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_PartnerCurrentAccountId",
                table: "AccountingSettings",
                column: "PartnerCurrentAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_PartnerCurrentAccountId",
                table: "AccountingSettings",
                column: "PartnerCurrentAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_PartnerCurrentAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_PartnerCurrentAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "PartnerCurrentAccountId",
                table: "AccountingSettings");
        }
    }
}
