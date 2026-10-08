using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTransportOutcomeSourceAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReversalOfInventoryMovementId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LossEventSourceAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LossEventId = table.Column<int>(type: "integer", nullable: false),
                    TransportLegId = table.Column<int>(type: "integer", nullable: true),
                    SourcePurchaseContractId = table.Column<int>(type: "integer", nullable: false),
                    SourceLoadingReceiptId = table.Column<int>(type: "integer", nullable: true),
                    SourceInventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    SourceTransportLegId = table.Column<int>(type: "integer", nullable: true),
                    SourceTransportReceiptId = table.Column<int>(type: "integer", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LossEventSourceAllocations", x => x.Id);
                    table.CheckConstraint("CK_LossEventSourceAllocations_QuantityPositive", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_Contracts_SourcePurchaseContract~",
                        column: x => x.SourcePurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_InventoryMovements_SourceInvento~",
                        column: x => x.SourceInventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_InventoryTransportLegs_SourceTra~",
                        column: x => x.SourceTransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_InventoryTransportLegs_Transport~",
                        column: x => x.TransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_InventoryTransportReceipts_Sourc~",
                        column: x => x.SourceTransportReceiptId,
                        principalTable: "InventoryTransportReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_LoadingReceipts_SourceLoadingRec~",
                        column: x => x.SourceLoadingReceiptId,
                        principalTable: "LoadingReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEventSourceAllocations_LossEvents_LossEventId",
                        column: x => x.LossEventId,
                        principalTable: "LossEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalesTransactionSourceAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: false),
                    TransportLegId = table.Column<int>(type: "integer", nullable: true),
                    SourcePurchaseContractId = table.Column<int>(type: "integer", nullable: false),
                    SourceLoadingReceiptId = table.Column<int>(type: "integer", nullable: true),
                    SourceInventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    SourceTransportLegId = table.Column<int>(type: "integer", nullable: true),
                    SourceTransportReceiptId = table.Column<int>(type: "integer", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTransactionSourceAllocations", x => x.Id);
                    table.CheckConstraint("CK_SalesTransactionSourceAllocations_QuantityPositive", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_Contracts_SourcePurchaseC~",
                        column: x => x.SourcePurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_InventoryMovements_Source~",
                        column: x => x.SourceInventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_InventoryTransportLegs_So~",
                        column: x => x.SourceTransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_InventoryTransportLegs_Tr~",
                        column: x => x.TransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_InventoryTransportReceipt~",
                        column: x => x.SourceTransportReceiptId,
                        principalTable: "InventoryTransportReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_LoadingReceipts_SourceLoa~",
                        column: x => x.SourceLoadingReceiptId,
                        principalTable: "LoadingReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTransactionSourceAllocations_SalesTransactions_SalesTr~",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ReversalOfInventoryMovementId",
                table: "InventoryMovements",
                column: "ReversalOfInventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_LossEventId",
                table: "LossEventSourceAllocations",
                column: "LossEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_SourceInventoryMovementId",
                table: "LossEventSourceAllocations",
                column: "SourceInventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_SourceLoadingReceiptId",
                table: "LossEventSourceAllocations",
                column: "SourceLoadingReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_SourcePurchaseContractId",
                table: "LossEventSourceAllocations",
                column: "SourcePurchaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_SourceTransportLegId",
                table: "LossEventSourceAllocations",
                column: "SourceTransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_SourceTransportReceiptId",
                table: "LossEventSourceAllocations",
                column: "SourceTransportReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEventSourceAllocations_TransportLegId",
                table: "LossEventSourceAllocations",
                column: "TransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SalesTransactionId",
                table: "SalesTransactionSourceAllocations",
                column: "SalesTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SourceInventoryMovementId",
                table: "SalesTransactionSourceAllocations",
                column: "SourceInventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SourceLoadingReceiptId",
                table: "SalesTransactionSourceAllocations",
                column: "SourceLoadingReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SourcePurchaseContractId",
                table: "SalesTransactionSourceAllocations",
                column: "SourcePurchaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SourceTransportLegId",
                table: "SalesTransactionSourceAllocations",
                column: "SourceTransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_SourceTransportReceiptId",
                table: "SalesTransactionSourceAllocations",
                column: "SourceTransportReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactionSourceAllocations_TransportLegId",
                table: "SalesTransactionSourceAllocations",
                column: "TransportLegId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryMovements_ReversalOfInventoryMo~",
                table: "InventoryMovements",
                column: "ReversalOfInventoryMovementId",
                principalTable: "InventoryMovements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryMovements_ReversalOfInventoryMo~",
                table: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "LossEventSourceAllocations");

            migrationBuilder.DropTable(
                name: "SalesTransactionSourceAllocations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_ReversalOfInventoryMovementId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "ReversalOfInventoryMovementId",
                table: "InventoryMovements");
        }
    }
}
