using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeWaySettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThreeWaySettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SettlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PayeeType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    SarrafId = table.Column<int>(type: "integer", nullable: true),
                    OtherPayeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CustomerPaidAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SupplierAcceptedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    CustomerPaidUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SupplierAcceptedUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    DifferenceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    DifferenceReason = table.Column<int>(type: "integer", nullable: true),
                    CustomerSaleContractId = table.Column<int>(type: "integer", nullable: true),
                    SupplierPurchaseContractId = table.Column<int>(type: "integer", nullable: true),
                    HawalaReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CustomerLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    SupplierLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreeWaySettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_Contracts_CustomerSaleContractId",
                        column: x => x.CustomerSaleContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_Contracts_SupplierPurchaseContractId",
                        column: x => x.SupplierPurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_LedgerEntries_CustomerLedgerEntryId",
                        column: x => x.CustomerLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_LedgerEntries_SupplierLedgerEntryId",
                        column: x => x.SupplierLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_Sarrafs_SarrafId",
                        column: x => x.SarrafId,
                        principalTable: "Sarrafs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ThreeWaySettlements_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_CustomerId",
                table: "ThreeWaySettlements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_CustomerLedgerEntryId",
                table: "ThreeWaySettlements",
                column: "CustomerLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_CustomerSaleContractId",
                table: "ThreeWaySettlements",
                column: "CustomerSaleContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_HawalaReference",
                table: "ThreeWaySettlements",
                column: "HawalaReference");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_PayeeType",
                table: "ThreeWaySettlements",
                column: "PayeeType");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_SarrafId",
                table: "ThreeWaySettlements",
                column: "SarrafId");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_SettlementDate",
                table: "ThreeWaySettlements",
                column: "SettlementDate");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_Status",
                table: "ThreeWaySettlements",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_SupplierId",
                table: "ThreeWaySettlements",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_SupplierLedgerEntryId",
                table: "ThreeWaySettlements",
                column: "SupplierLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreeWaySettlements_SupplierPurchaseContractId",
                table: "ThreeWaySettlements",
                column: "SupplierPurchaseContractId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThreeWaySettlements");
        }
    }
}
