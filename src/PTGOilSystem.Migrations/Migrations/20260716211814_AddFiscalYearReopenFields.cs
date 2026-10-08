using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalYearReopenFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReopenReason",
                table: "FiscalYears",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReopenedAt",
                table: "FiscalYears",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReopenedByUserId",
                table: "FiscalYears",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_ReopenedByUserId",
                table: "FiscalYears",
                column: "ReopenedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYears_Users_ReopenedByUserId",
                table: "FiscalYears",
                column: "ReopenedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiscalYears_Users_ReopenedByUserId",
                table: "FiscalYears");

            migrationBuilder.DropIndex(
                name: "IX_FiscalYears_ReopenedByUserId",
                table: "FiscalYears");

            migrationBuilder.DropColumn(
                name: "ReopenReason",
                table: "FiscalYears");

            migrationBuilder.DropColumn(
                name: "ReopenedAt",
                table: "FiscalYears");

            migrationBuilder.DropColumn(
                name: "ReopenedByUserId",
                table: "FiscalYears");
        }
    }
}
