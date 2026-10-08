using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddContractPlattsEnhancement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BenchmarkCode",
                table: "Contracts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlattsBasisDate",
                table: "Contracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlattsBasisMonth",
                table: "Contracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlattsManualPriceUsd",
                table: "Contracts",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlattsPeriodType",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PremiumDiscountUsd",
                table: "Contracts",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Contracts"
                SET "PremiumDiscountUsd" = "PremiumUsd"
                WHERE "PremiumDiscountUsd" IS NULL
                  AND "PremiumUsd" IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "PlattsMonthlyManuals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    BenchmarkCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Month = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PriceUsdPerMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlattsMonthlyManuals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlattsMonthlyManuals_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlattsMonthlyManuals_ProductId_BenchmarkCode_Month",
                table: "PlattsMonthlyManuals",
                columns: new[] { "ProductId", "BenchmarkCode", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlattsMonthlyManuals");

            migrationBuilder.DropColumn(
                name: "BenchmarkCode",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PlattsBasisDate",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PlattsBasisMonth",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PlattsManualPriceUsd",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PlattsPeriodType",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PremiumDiscountUsd",
                table: "Contracts");
        }
    }
}
