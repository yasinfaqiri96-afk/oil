using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerDualCurrencyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AppliedFxRateDate",
                table: "LedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliedFxRateSource",
                table: "LedgerEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AppliedFxRateToUsd",
                table: "LedgerEntries",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceAmount",
                table: "LedgerEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCurrencyCode",
                table: "LedgerEntries",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_EntryDate",
                table: "LedgerEntries",
                column: "EntryDate");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_SourceCurrencyCode",
                table: "LedgerEntries",
                column: "SourceCurrencyCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_EntryDate",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_SourceCurrencyCode",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "AppliedFxRateDate",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "AppliedFxRateSource",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "AppliedFxRateToUsd",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "SourceAmount",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "SourceCurrencyCode",
                table: "LedgerEntries");
        }
    }
}
