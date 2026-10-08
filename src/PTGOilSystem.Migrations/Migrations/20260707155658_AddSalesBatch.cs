using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesBatchId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    SaleDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "USD"),
                    AppliedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    UnitPriceInCurrency = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalInCurrency = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PaymentNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesBatches_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_SalesBatchId",
                table: "SalesTransactions",
                column: "SalesBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesBatches_BatchNumber",
                table: "SalesBatches",
                column: "BatchNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SalesBatches_CustomerId",
                table: "SalesBatches",
                column: "CustomerId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_SalesBatches_SalesBatchId",
                table: "SalesTransactions",
                column: "SalesBatchId",
                principalTable: "SalesBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_SalesBatches_SalesBatchId",
                table: "SalesTransactions");

            migrationBuilder.DropTable(
                name: "SalesBatches");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_SalesBatchId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SalesBatchId",
                table: "SalesTransactions");
        }
    }
}
