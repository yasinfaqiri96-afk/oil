using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryTransportReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InventoryTransportLegId = table.Column<int>(type: "integer", nullable: false),
                    ReceiptDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ShortageQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReceiptDestination = table.Column<int>(type: "integer", nullable: false),
                    DestinationTerminalId = table.Column<int>(type: "integer", nullable: true),
                    DestinationStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransportReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransportReceipts_InventoryMovements_InventoryMove~",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportReceipts_InventoryTransportLegs_Inventory~",
                        column: x => x.InventoryTransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportReceipts_SalesTransactions_SalesTransacti~",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportReceipts_StorageTanks_DestinationStorageT~",
                        column: x => x.DestinationStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportReceipts_Terminals_DestinationTerminalId",
                        column: x => x.DestinationTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_DestinationStorageTankId",
                table: "InventoryTransportReceipts",
                column: "DestinationStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_DestinationTerminalId",
                table: "InventoryTransportReceipts",
                column: "DestinationTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_InventoryMovementId",
                table: "InventoryTransportReceipts",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_InventoryTransportLegId",
                table: "InventoryTransportReceipts",
                column: "InventoryTransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_ReceiptDate",
                table: "InventoryTransportReceipts",
                column: "ReceiptDate");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_ReceiptDestination",
                table: "InventoryTransportReceipts",
                column: "ReceiptDestination");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_SalesTransactionId",
                table: "InventoryTransportReceipts",
                column: "SalesTransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryTransportReceipts");
        }
    }
}
