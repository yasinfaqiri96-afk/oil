using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAverageCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryAverageCosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAverageCosts", x => x.Id);
                    table.CheckConstraint("CK_InventoryAverageCosts_NonNegative", "\"QuantityMt\" >= 0 AND \"TotalValueUsd\" >= 0");
                    table.ForeignKey(
                        name: "FK_InventoryAverageCosts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAverageCosts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAverageCosts_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAverageCosts_CompanyId_ProductId_TerminalId",
                table: "InventoryAverageCosts",
                columns: new[] { "CompanyId", "ProductId", "TerminalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAverageCosts_ProductId",
                table: "InventoryAverageCosts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAverageCosts_TerminalId",
                table: "InventoryAverageCosts",
                column: "TerminalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryAverageCosts");
        }
    }
}
