using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPreSaleOrderAndDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreSaleOrderId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreSaleOrderId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreSaleOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    OrderDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedDeliveryFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpectedDeliveryTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "USD"),
                    UnitPriceInCurrency = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AppliedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    UnitPriceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalInCurrency = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentTerms = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreSaleOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreSaleOrders_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreSaleOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreSaleOrders_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_PreSaleOrderId",
                table: "SalesTransactions",
                column: "PreSaleOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_PreSaleOrderId",
                table: "PaymentTransactions",
                column: "PreSaleOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PreSaleOrders_CompanyId",
                table: "PreSaleOrders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PreSaleOrders_CustomerId_Status",
                table: "PreSaleOrders",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PreSaleOrders_OrderNumber",
                table: "PreSaleOrders",
                column: "OrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_PreSaleOrders_ProductId",
                table: "PreSaleOrders",
                column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_PreSaleOrders_PreSaleOrderId",
                table: "SalesTransactions",
                column: "PreSaleOrderId",
                principalTable: "PreSaleOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_PreSaleOrders_PreSaleOrderId",
                table: "SalesTransactions");

            migrationBuilder.DropTable(
                name: "PreSaleOrders");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_PreSaleOrderId",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_PreSaleOrderId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "PreSaleOrderId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "PreSaleOrderId",
                table: "PaymentTransactions");
        }
    }
}
