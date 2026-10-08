using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleCorrectionChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "SalesTransactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                table: "SalesTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledByUserId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CorrectedFromSaleId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementSaleId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_CorrectedFromSaleId",
                table: "SalesTransactions",
                column: "CorrectedFromSaleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_ReplacementSaleId",
                table: "SalesTransactions",
                column: "ReplacementSaleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_CorrectedFromSaleId",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_ReplacementSaleId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "CorrectedFromSaleId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "ReplacementSaleId",
                table: "SalesTransactions");
        }
    }
}
