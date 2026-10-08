using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierPaymentAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    AllocationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllocatedPaymentAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PaymentCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PaymentFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AllocatedBookAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ContractCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ContractCurrencyPerUsdRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ContractCurrencyFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AllocatedContractCurrencyAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReversalOfAllocationId = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_SupplierPaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocations_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocations_PaymentTransactions_PaymentTrans~",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_AllocationDate",
                table: "SupplierPaymentAllocations",
                column: "AllocationDate");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_ContractId_Status",
                table: "SupplierPaymentAllocations",
                columns: new[] { "ContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_PaymentTransactionId_Status",
                table: "SupplierPaymentAllocations",
                columns: new[] { "PaymentTransactionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierPaymentAllocations");
        }
    }
}
