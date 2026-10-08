using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingReceiptAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoadingReceiptAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoadingReceiptId = table.Column<int>(type: "integer", nullable: false),
                    Destination = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SourcePurchaseContractId = table.Column<int>(type: "integer", nullable: true),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    TruckDispatchId = table.Column<int>(type: "integer", nullable: true),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: true),
                    ReferenceDocument = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadingReceiptAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_Contracts_SourcePurchaseContractId",
                        column: x => x.SourcePurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_InventoryMovements_InventoryMovem~",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_LoadingReceipts_LoadingReceiptId",
                        column: x => x.LoadingReceiptId,
                        principalTable: "LoadingReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_SalesTransactions_SalesTransactio~",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceiptAllocations_TruckDispatches_TruckDispatchId",
                        column: x => x.TruckDispatchId,
                        principalTable: "TruckDispatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_InventoryMovementId",
                table: "LoadingReceiptAllocations",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_LoadingReceiptId",
                table: "LoadingReceiptAllocations",
                column: "LoadingReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_SalesTransactionId",
                table: "LoadingReceiptAllocations",
                column: "SalesTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_SourcePurchaseContractId",
                table: "LoadingReceiptAllocations",
                column: "SourcePurchaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_StorageTankId",
                table: "LoadingReceiptAllocations",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_TerminalId",
                table: "LoadingReceiptAllocations",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_TruckDispatchId",
                table: "LoadingReceiptAllocations",
                column: "TruckDispatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoadingReceiptAllocations");
        }
    }
}
