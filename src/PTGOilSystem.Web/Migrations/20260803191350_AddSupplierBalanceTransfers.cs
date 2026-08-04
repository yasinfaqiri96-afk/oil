using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBalanceTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierBalanceTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    TransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TransferOriginalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OriginalCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HistoricalFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    HistoricalAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TransferPerUsdRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TransferFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TransferValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeDifferenceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeDifferenceType = table.Column<int>(type: "integer", nullable: false),
                    ExchangeDifferenceLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    ContractCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ContractCurrencyPerUsdRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ContractCurrencyFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TransferContractCurrencyAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBalanceTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceTransfers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceTransfers_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceTransfers_LedgerEntries_ExchangeDifferenceLe~",
                        column: x => x.ExchangeDifferenceLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceTransfers_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBalanceTransferSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierBalanceTransferId = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    SourceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedOriginalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OriginalCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HistoricalFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ConsumedBookAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBalanceTransferSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceTransferSources_SupplierBalanceTransfers_Sup~",
                        column: x => x.SupplierBalanceTransferId,
                        principalTable: "SupplierBalanceTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_BatchId",
                table: "SupplierBalanceTransfers",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_CompanyId",
                table: "SupplierBalanceTransfers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_ContractId_Status",
                table: "SupplierBalanceTransfers",
                columns: new[] { "ContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_ExchangeDifferenceLedgerEntryId",
                table: "SupplierBalanceTransfers",
                column: "ExchangeDifferenceLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_SupplierId_Status",
                table: "SupplierBalanceTransfers",
                columns: new[] { "SupplierId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransfers_TransferDate",
                table: "SupplierBalanceTransfers",
                column: "TransferDate");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransferSources_SourceType_SourceId",
                table: "SupplierBalanceTransferSources",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceTransferSources_SupplierBalanceTransferId",
                table: "SupplierBalanceTransferSources",
                column: "SupplierBalanceTransferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierBalanceTransferSources");

            migrationBuilder.DropTable(
                name: "SupplierBalanceTransfers");
        }
    }
}
