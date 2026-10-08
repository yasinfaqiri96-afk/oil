using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleStagesAndLossEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SaleStage",
                table: "SalesTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LossEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    ShipmentId = table.Column<int>(type: "integer", nullable: true),
                    LoadingRegisterId = table.Column<int>(type: "integer", nullable: true),
                    LoadingReceiptId = table.Column<int>(type: "integer", nullable: true),
                    TruckDispatchId = table.Column<int>(type: "integer", nullable: true),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: true),
                    TerminalId = table.Column<int>(type: "integer", nullable: true),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ActualQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    DifferenceQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ToleranceQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AllowableLossMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ChargeableLossMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ResponsiblePartyType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResponsiblePartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FinancialTreatment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AffectsInventory = table.Column<bool>(type: "boolean", nullable: false),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LossEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LossEvents_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_InventoryMovements_InventoryMovementId",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LossEvents_LoadingReceipts_LoadingReceiptId",
                        column: x => x.LoadingReceiptId,
                        principalTable: "LoadingReceipts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_LoadingRegisters_LoadingRegisterId",
                        column: x => x.LoadingRegisterId,
                        principalTable: "LoadingRegisters",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LossEvents_SalesTransactions_SalesTransactionId",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LossEvents_TruckDispatches_TruckDispatchId",
                        column: x => x.TruckDispatchId,
                        principalTable: "TruckDispatches",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_ContractId",
                table: "LossEvents",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_EventDate_Stage",
                table: "LossEvents",
                columns: new[] { "EventDate", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_InventoryMovementId",
                table: "LossEvents",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_LoadingReceiptId",
                table: "LossEvents",
                column: "LoadingReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_LoadingRegisterId",
                table: "LossEvents",
                column: "LoadingRegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_ProductId",
                table: "LossEvents",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_SalesTransactionId",
                table: "LossEvents",
                column: "SalesTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_ShipmentId",
                table: "LossEvents",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_StorageTankId",
                table: "LossEvents",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_TerminalId",
                table: "LossEvents",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_LossEvents_TruckDispatchId",
                table: "LossEvents",
                column: "TruckDispatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LossEvents");

            migrationBuilder.DropColumn(
                name: "SaleStage",
                table: "SalesTransactions");
        }
    }
}
