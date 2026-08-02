using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingReceiptSoftCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "LoadingReceipts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                table: "LoadingReceipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledByUserId",
                table: "LoadingReceipts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "LoadingReceipts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceipts_IsCancelled",
                table: "LoadingReceipts",
                column: "IsCancelled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoadingReceipts_IsCancelled",
                table: "LoadingReceipts");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "LoadingReceipts");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                table: "LoadingReceipts");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "LoadingReceipts");

            migrationBuilder.DropColumn(
                name: "IsCancelled",
                table: "LoadingReceipts");
        }
    }
}
