using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryLots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RemainingQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RootShipmentId = table.Column<int>(type: "integer", nullable: true),
                    RootContractId = table.Column<int>(type: "integer", nullable: true),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    ParentLotId = table.Column<int>(type: "integer", nullable: true),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceReferenceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SourceReferenceId = table.Column<int>(type: "integer", nullable: true),
                    CreatedFromMovementId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LineageConfidence = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLots", x => x.Id);
                    table.CheckConstraint("CK_InventoryLots_QuantityNonNegative", "\"QuantityMt\" >= 0");
                    table.CheckConstraint("CK_InventoryLots_RemainingLeQuantity", "\"RemainingQuantityMt\" <= \"QuantityMt\"");
                    table.CheckConstraint("CK_InventoryLots_RemainingNonNegative", "\"RemainingQuantityMt\" >= 0");
                    table.ForeignKey(
                        name: "FK_InventoryLots_Contracts_RootContractId",
                        column: x => x.RootContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_InventoryLots_ParentLotId",
                        column: x => x.ParentLotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_InventoryMovements_CreatedFromMovementId",
                        column: x => x.CreatedFromMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_Shipments_RootShipmentId",
                        column: x => x.RootShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLots_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseLotAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseTransactionId = table.Column<int>(type: "integer", nullable: false),
                    LotId = table.Column<int>(type: "integer", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AllocationMethod = table.Column<int>(type: "integer", nullable: false),
                    LineageConfidence = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLotAllocations", x => x.Id);
                    table.CheckConstraint("CK_ExpenseLotAllocations_AmountNonNegative", "\"AmountUsd\" >= 0");
                    table.ForeignKey(
                        name: "FK_ExpenseLotAllocations_ExpenseTransactions_ExpenseTransactio~",
                        column: x => x.ExpenseTransactionId,
                        principalTable: "ExpenseTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseLotAllocations_InventoryLots_LotId",
                        column: x => x.LotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryLotMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromLotId = table.Column<int>(type: "integer", nullable: true),
                    ToLotId = table.Column<int>(type: "integer", nullable: true),
                    MovementKind = table.Column<int>(type: "integer", nullable: false),
                    FromTerminalId = table.Column<int>(type: "integer", nullable: true),
                    FromStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    ToTerminalId = table.Column<int>(type: "integer", nullable: true),
                    ToStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    VehicleType = table.Column<int>(type: "integer", nullable: true),
                    VehicleRefType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VehicleRefId = table.Column<int>(type: "integer", nullable: true),
                    LoadedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReceivedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    ShortageQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    MovementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ShipmentId = table.Column<int>(type: "integer", nullable: true),
                    SourceReferenceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SourceReferenceId = table.Column<int>(type: "integer", nullable: true),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    LineageConfidence = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLotMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_InventoryLots_FromLotId",
                        column: x => x.FromLotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_InventoryLots_ToLotId",
                        column: x => x.ToLotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_InventoryMovements_InventoryMovementId",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_StorageTanks_FromStorageTankId",
                        column: x => x.FromStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_StorageTanks_ToStorageTankId",
                        column: x => x.ToStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_Terminals_FromTerminalId",
                        column: x => x.FromTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLotMovements_Terminals_ToTerminalId",
                        column: x => x.ToTerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LossLotAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LossEventId = table.Column<int>(type: "integer", nullable: false),
                    LotId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    AllocationMethod = table.Column<int>(type: "integer", nullable: false),
                    LineageConfidence = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LossLotAllocations", x => x.Id);
                    table.CheckConstraint("CK_LossLotAllocations_QuantityNonNegative", "\"QuantityMt\" >= 0");
                    table.ForeignKey(
                        name: "FK_LossLotAllocations_InventoryLots_LotId",
                        column: x => x.LotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossLotAllocations_LossEvents_LossEventId",
                        column: x => x.LossEventId,
                        principalTable: "LossEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleLotAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: false),
                    LotId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    UnitCostUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    AllocationMethod = table.Column<int>(type: "integer", nullable: false),
                    LineageConfidence = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleLotAllocations", x => x.Id);
                    table.CheckConstraint("CK_SaleLotAllocations_QuantityNonNegative", "\"QuantityMt\" >= 0");
                    table.ForeignKey(
                        name: "FK_SaleLotAllocations_InventoryLots_LotId",
                        column: x => x.LotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleLotAllocations_SalesTransactions_SalesTransactionId",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLotAllocations_ExpenseTransactionId",
                table: "ExpenseLotAllocations",
                column: "ExpenseTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLotAllocations_LotId",
                table: "ExpenseLotAllocations",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_FromLotId",
                table: "InventoryLotMovements",
                column: "FromLotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_FromStorageTankId",
                table: "InventoryLotMovements",
                column: "FromStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_FromTerminalId",
                table: "InventoryLotMovements",
                column: "FromTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_InventoryMovementId",
                table: "InventoryLotMovements",
                column: "InventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_MovementDate",
                table: "InventoryLotMovements",
                column: "MovementDate");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_MovementKind",
                table: "InventoryLotMovements",
                column: "MovementKind");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_ShipmentId",
                table: "InventoryLotMovements",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_SourceReferenceType_SourceReferenceId",
                table: "InventoryLotMovements",
                columns: new[] { "SourceReferenceType", "SourceReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_ToLotId",
                table: "InventoryLotMovements",
                column: "ToLotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_ToStorageTankId",
                table: "InventoryLotMovements",
                column: "ToStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLotMovements_ToTerminalId",
                table: "InventoryLotMovements",
                column: "ToTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_CreatedFromMovementId",
                table: "InventoryLots",
                column: "CreatedFromMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_ParentLotId",
                table: "InventoryLots",
                column: "ParentLotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_ProductId_TerminalId_StorageTankId_Status",
                table: "InventoryLots",
                columns: new[] { "ProductId", "TerminalId", "StorageTankId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_RootContractId",
                table: "InventoryLots",
                column: "RootContractId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_RootShipmentId",
                table: "InventoryLots",
                column: "RootShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_SourceReferenceType_SourceReferenceId",
                table: "InventoryLots",
                columns: new[] { "SourceReferenceType", "SourceReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_StorageTankId",
                table: "InventoryLots",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_SupplierId",
                table: "InventoryLots",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_TerminalId",
                table: "InventoryLots",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_LossLotAllocations_LossEventId",
                table: "LossLotAllocations",
                column: "LossEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LossLotAllocations_LotId",
                table: "LossLotAllocations",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleLotAllocations_LotId",
                table: "SaleLotAllocations",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleLotAllocations_SalesTransactionId",
                table: "SaleLotAllocations",
                column: "SalesTransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseLotAllocations");

            migrationBuilder.DropTable(
                name: "InventoryLotMovements");

            migrationBuilder.DropTable(
                name: "LossLotAllocations");

            migrationBuilder.DropTable(
                name: "SaleLotAllocations");

            migrationBuilder.DropTable(
                name: "InventoryLots");
        }
    }
}
