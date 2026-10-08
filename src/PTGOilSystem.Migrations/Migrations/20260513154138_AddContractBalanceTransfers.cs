using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddContractBalanceTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractBalanceTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FromContractId = table.Column<int>(type: "integer", nullable: false),
                    ToContractId = table.Column<int>(type: "integer", nullable: false),
                    AmountOriginal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    FxRateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FxRateSource = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OriginalPaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    OriginalPaymentFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractBalanceTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractBalanceTransfers_Contracts_FromContractId",
                        column: x => x.FromContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractBalanceTransfers_Contracts_ToContractId",
                        column: x => x.ToContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractBalanceTransfers_PaymentTransactions_OriginalPaymen~",
                        column: x => x.OriginalPaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractBalanceTransfers_FromContractId_TransferDate",
                table: "ContractBalanceTransfers",
                columns: new[] { "FromContractId", "TransferDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractBalanceTransfers_IsCancelled",
                table: "ContractBalanceTransfers",
                column: "IsCancelled");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBalanceTransfers_OriginalPaymentTransactionId",
                table: "ContractBalanceTransfers",
                column: "OriginalPaymentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBalanceTransfers_Reference",
                table: "ContractBalanceTransfers",
                column: "Reference");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBalanceTransfers_ToContractId_TransferDate",
                table: "ContractBalanceTransfers",
                columns: new[] { "ToContractId", "TransferDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractBalanceTransfers");
        }
    }
}
