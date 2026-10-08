using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CapacityMt",
                table: "InventoryTransportLegs",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierType",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FreightAmount",
                table: "InventoryTransportLegs",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FreightCurrencyId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InventoryTransportBatchId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TruckId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WagonId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryTransportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTerminalId = table.Column<int>(type: "integer", nullable: false),
                    SourceStorageTankId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TotalQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TransportDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TransportGroupKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransportBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransportBatches_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportBatches_StorageTanks_SourceStorageTankId",
                        column: x => x.SourceStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportBatches_Terminals_SourceTerminalId",
                        column: x => x.SourceTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransportLegAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InventoryTransportLegId = table.Column<int>(type: "integer", nullable: false),
                    SourcePurchaseContractId = table.Column<int>(type: "integer", nullable: false),
                    SourceLoadingReceiptId = table.Column<int>(type: "integer", nullable: true),
                    SourceInventoryMovementId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OutboundInventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransportLegAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegAllocations_Contracts_SourcePurchaseCo~",
                        column: x => x.SourcePurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegAllocations_InventoryMovements_Outboun~",
                        column: x => x.OutboundInventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegAllocations_InventoryMovements_SourceI~",
                        column: x => x.SourceInventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegAllocations_InventoryTransportLegs_Inv~",
                        column: x => x.InventoryTransportLegId,
                        principalTable: "InventoryTransportLegs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegAllocations_LoadingReceipts_SourceLoad~",
                        column: x => x.SourceLoadingReceiptId,
                        principalTable: "LoadingReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_FreightCurrencyId",
                table: "InventoryTransportLegs",
                column: "FreightCurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_InventoryTransportBatchId",
                table: "InventoryTransportLegs",
                column: "InventoryTransportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_TruckId",
                table: "InventoryTransportLegs",
                column: "TruckId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_WagonId",
                table: "InventoryTransportLegs",
                column: "WagonId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_BatchNumber",
                table: "InventoryTransportBatches",
                column: "BatchNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_ProductId",
                table: "InventoryTransportBatches",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_SourceStorageTankId",
                table: "InventoryTransportBatches",
                column: "SourceStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_SourceTerminalId_SourceStorageTan~",
                table: "InventoryTransportBatches",
                columns: new[] { "SourceTerminalId", "SourceStorageTankId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_Status",
                table: "InventoryTransportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportBatches_TransportGroupKey",
                table: "InventoryTransportBatches",
                column: "TransportGroupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_InventoryTransportLegId",
                table: "InventoryTransportLegAllocations",
                column: "InventoryTransportLegId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_OutboundInventoryMovementId",
                table: "InventoryTransportLegAllocations",
                column: "OutboundInventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourceInventoryMovementId",
                table: "InventoryTransportLegAllocations",
                column: "SourceInventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourceLoadingReceiptId",
                table: "InventoryTransportLegAllocations",
                column: "SourceLoadingReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegAllocations_SourcePurchaseContractId",
                table: "InventoryTransportLegAllocations",
                column: "SourcePurchaseContractId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Currencies_FreightCurrencyId",
                table: "InventoryTransportLegs",
                column: "FreightCurrencyId",
                principalTable: "Currencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_InventoryTransportBatches_InventoryT~",
                table: "InventoryTransportLegs",
                column: "InventoryTransportBatchId",
                principalTable: "InventoryTransportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Trucks_TruckId",
                table: "InventoryTransportLegs",
                column: "TruckId",
                principalTable: "Trucks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransportLegs_Wagons_WagonId",
                table: "InventoryTransportLegs",
                column: "WagonId",
                principalTable: "Wagons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Currencies_FreightCurrencyId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_InventoryTransportBatches_InventoryT~",
                table: "InventoryTransportLegs");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Trucks_TruckId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransportLegs_Wagons_WagonId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropTable(
                name: "InventoryTransportBatches");

            migrationBuilder.DropTable(
                name: "InventoryTransportLegAllocations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_FreightCurrencyId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_InventoryTransportBatchId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_TruckId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_WagonId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "CapacityMt",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "CarrierType",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "FreightAmount",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "FreightCurrencyId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "InventoryTransportBatchId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "TruckId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "WagonId",
                table: "InventoryTransportLegs");
        }
    }
}
