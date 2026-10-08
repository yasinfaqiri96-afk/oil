using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddViaSarrafGroupIdToLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ViaSarrafGroupId",
                table: "LedgerEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ViaSarrafGroupId",
                table: "LedgerEntries",
                column: "ViaSarrafGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ViaSarrafGroupId_SourceType",
                table: "LedgerEntries",
                columns: new[] { "ViaSarrafGroupId", "SourceType" },
                unique: true,
                filter: "\"ViaSarrafGroupId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_ViaSarrafGroupId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_ViaSarrafGroupId_SourceType",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "ViaSarrafGroupId",
                table: "LedgerEntries");
        }
    }
}
