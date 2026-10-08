using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryTransportLegs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryTransportLegs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourcePurchaseContractId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    SourceTerminalId = table.Column<int>(type: "integer", nullable: false),
                    SourceStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    DestinationTerminalId = table.Column<int>(type: "integer", nullable: true),
                    DestinationStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    DestinationLocationId = table.Column<int>(type: "integer", nullable: true),
                    TransportType = table.Column<int>(type: "integer", nullable: false),
                    WagonNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RwbNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BillOfLadingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RouteDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LoadedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedArrivalDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ChargeableQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    OutboundInventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransportLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_Contracts_SourcePurchaseContractId",
                        column: x => x.SourcePurchaseContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_InventoryMovements_OutboundInventory~",
                        column: x => x.OutboundInventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_Locations_DestinationLocationId",
                        column: x => x.DestinationLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_StorageTanks_DestinationStorageTankId",
                        column: x => x.DestinationStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_StorageTanks_SourceStorageTankId",
                        column: x => x.SourceStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_Terminals_DestinationTerminalId",
                        column: x => x.DestinationTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransportLegs_Terminals_SourceTerminalId",
                        column: x => x.SourceTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_DestinationLocationId",
                table: "InventoryTransportLegs",
                column: "DestinationLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_DestinationStorageTankId",
                table: "InventoryTransportLegs",
                column: "DestinationStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_DestinationTerminalId",
                table: "InventoryTransportLegs",
                column: "DestinationTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_LoadedDate",
                table: "InventoryTransportLegs",
                column: "LoadedDate");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_OutboundInventoryMovementId",
                table: "InventoryTransportLegs",
                column: "OutboundInventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_ProductId",
                table: "InventoryTransportLegs",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_RwbNo",
                table: "InventoryTransportLegs",
                column: "RwbNo");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_SourcePurchaseContractId",
                table: "InventoryTransportLegs",
                column: "SourcePurchaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_SourceStorageTankId",
                table: "InventoryTransportLegs",
                column: "SourceStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_SourceTerminalId_SourceStorageTankId",
                table: "InventoryTransportLegs",
                columns: new[] { "SourceTerminalId", "SourceStorageTankId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_Status",
                table: "InventoryTransportLegs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_WagonNumber",
                table: "InventoryTransportLegs",
                column: "WagonNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryTransportLegs");
        }
    }
}
