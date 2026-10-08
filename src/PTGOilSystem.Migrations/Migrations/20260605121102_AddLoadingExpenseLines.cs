using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingExpenseLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoadingExpenseLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoadingRegisterId = table.Column<int>(type: "integer", nullable: false),
                    ExpenseTypeId = table.Column<int>(type: "integer", nullable: false),
                    CalculationMode = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    UnitRateUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PartyType = table.Column<int>(type: "integer", nullable: false),
                    ServiceProviderId = table.Column<int>(type: "integer", nullable: true),
                    OperationalAssetId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExpenseTransactionId = table.Column<int>(type: "integer", nullable: true),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    AssetRentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadingExpenseLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_AssetRentTransactions_AssetRentTransact~",
                        column: x => x.AssetRentTransactionId,
                        principalTable: "AssetRentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_ExpenseTransactions_ExpenseTransactionId",
                        column: x => x.ExpenseTransactionId,
                        principalTable: "ExpenseTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_ExpenseTypes_ExpenseTypeId",
                        column: x => x.ExpenseTypeId,
                        principalTable: "ExpenseTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_LoadingRegisters_LoadingRegisterId",
                        column: x => x.LoadingRegisterId,
                        principalTable: "LoadingRegisters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_OperationalAssets_OperationalAssetId",
                        column: x => x.OperationalAssetId,
                        principalTable: "OperationalAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingExpenseLines_ServiceProviders_ServiceProviderId",
                        column: x => x.ServiceProviderId,
                        principalTable: "ServiceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_AssetRentTransactionId",
                table: "LoadingExpenseLines",
                column: "AssetRentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_ExpenseTransactionId",
                table: "LoadingExpenseLines",
                column: "ExpenseTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_ExpenseTypeId",
                table: "LoadingExpenseLines",
                column: "ExpenseTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_LoadingRegisterId",
                table: "LoadingExpenseLines",
                column: "LoadingRegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_OperationalAssetId",
                table: "LoadingExpenseLines",
                column: "OperationalAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_ServiceProviderId",
                table: "LoadingExpenseLines",
                column: "ServiceProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoadingExpenseLines");
        }
    }
}
