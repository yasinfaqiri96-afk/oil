using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSarrafSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SarrafId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Sarrafs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sarrafs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SarrafSettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SettlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SarrafId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    CashAccountId = table.Column<int>(type: "integer", nullable: true),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RequestedCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RequestedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RequestedAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SarrafCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SarrafRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    SarrafChargedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SarrafFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    SarrafChargedAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SupplierAcceptedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SupplierAcceptedCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SupplierAcceptedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    SupplierAcceptedAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SupplierRate = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    DifferenceAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    DifferenceType = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DifferenceTreatment = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    ExchangeDifferenceLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SarrafSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_LedgerEntries_ExchangeDifferenceLedgerEnt~",
                        column: x => x.ExchangeDifferenceLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_LedgerEntries_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_PaymentTransactions_PaymentTransactionId",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_Sarrafs_SarrafId",
                        column: x => x.SarrafId,
                        principalTable: "Sarrafs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SarrafSettlements_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_SarrafId",
                table: "PaymentTransactions",
                column: "SarrafId");

            migrationBuilder.CreateIndex(
                name: "IX_Sarrafs_IsActive",
                table: "Sarrafs",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Sarrafs_Name",
                table: "Sarrafs",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_CashAccountId",
                table: "SarrafSettlements",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_ContractId",
                table: "SarrafSettlements",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_ExchangeDifferenceLedgerEntryId",
                table: "SarrafSettlements",
                column: "ExchangeDifferenceLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_LedgerEntryId",
                table: "SarrafSettlements",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_PaymentTransactionId",
                table: "SarrafSettlements",
                column: "PaymentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_ReferenceNumber",
                table: "SarrafSettlements",
                column: "ReferenceNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_SarrafId",
                table: "SarrafSettlements",
                column: "SarrafId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_SettlementDate",
                table: "SarrafSettlements",
                column: "SettlementDate");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_Status",
                table: "SarrafSettlements",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_SupplierId",
                table: "SarrafSettlements",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_Sarrafs_SarrafId",
                table: "PaymentTransactions",
                column: "SarrafId",
                principalTable: "Sarrafs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_Sarrafs_SarrafId",
                table: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "SarrafSettlements");

            migrationBuilder.DropTable(
                name: "Sarrafs");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_SarrafId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "SarrafId",
                table: "PaymentTransactions");
        }
    }
}
