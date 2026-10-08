using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260503100000_AddGapCoverage")]
    public partial class AddGapCoverage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Gap #2: LoadingRegister — chargeable quantity + railway expense ---
            migrationBuilder.AddColumn<decimal>(
                name: "ChargeableQuantityMt",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RailwayRateUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RailwayExpenseUsd",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            // --- Gap #3: LoadingReceipt — arrival date + actual arrived qty ---
            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivalDate",
                table: "LoadingReceipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeakDate",
                table: "LoadingReceipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualArrivedQuantityMt",
                table: "LoadingReceipts",
                type: "numeric(18,4)",
                nullable: true);

            // --- Gap #4+#5: TruckDispatch — ticket serial + tolerance + chargeable shortage ---
            migrationBuilder.AddColumn<string>(
                name: "TicketSerialNumber",
                table: "TruckDispatches",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ToleranceMt",
                table: "TruckDispatches",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargeableShortageMt",
                table: "TruckDispatches",
                type: "numeric(18,4)",
                nullable: true);

            // --- Gap #4: SalesTransaction — ticket serial + stock source type ---
            migrationBuilder.AddColumn<string>(
                name: "TicketSerialNumber",
                table: "SalesTransactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockSourceType",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            // --- Gap #7: ShipmentContracts junction table ---
            migrationBuilder.CreateTable(
                name: "ShipmentContracts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShipmentId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentContracts_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentContracts_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentContracts_ShipmentId_ContractId",
                table: "ShipmentContracts",
                columns: new[] { "ShipmentId", "ContractId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentContracts_ContractId",
                table: "ShipmentContracts",
                column: "ContractId");

            // --- Gap #1: CustomsDeclarations ---
            migrationBuilder.CreateTable(
                name: "CustomsDeclarations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoadingRegisterId = table.Column<int>(type: "integer", nullable: false),
                    WagonOrTruckNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DeclarationReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DeclarationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsignmentWeightMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    TotalAfn = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RatePerMtAfn = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    RatePerMtUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomsDeclarations_LoadingRegisters_LoadingRegisterId",
                        column: x => x.LoadingRegisterId,
                        principalTable: "LoadingRegisters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_LoadingRegisterId",
                table: "CustomsDeclarations",
                column: "LoadingRegisterId");

            // --- Gap #1: CustomsDeclarationItems ---
            migrationBuilder.CreateTable(
                name: "CustomsDeclarationItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomsDeclarationId = table.Column<int>(type: "integer", nullable: false),
                    ComponentType = table.Column<int>(type: "integer", nullable: false),
                    CustomLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AmountAfn = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsDeclarationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomsDeclarationItems_CustomsDeclarations_CustomsDeclarationId",
                        column: x => x.CustomsDeclarationId,
                        principalTable: "CustomsDeclarations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarationItems_CustomsDeclarationId",
                table: "CustomsDeclarationItems",
                column: "CustomsDeclarationId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CustomsDeclarationItems");
            migrationBuilder.DropTable(name: "CustomsDeclarations");
            migrationBuilder.DropTable(name: "ShipmentContracts");

            migrationBuilder.DropColumn(name: "ChargeableQuantityMt", table: "LoadingRegisters");
            migrationBuilder.DropColumn(name: "RailwayRateUsd", table: "LoadingRegisters");
            migrationBuilder.DropColumn(name: "RailwayExpenseUsd", table: "LoadingRegisters");

            migrationBuilder.DropColumn(name: "ArrivalDate", table: "LoadingReceipts");
            migrationBuilder.DropColumn(name: "LeakDate", table: "LoadingReceipts");
            migrationBuilder.DropColumn(name: "ActualArrivedQuantityMt", table: "LoadingReceipts");

            migrationBuilder.DropColumn(name: "TicketSerialNumber", table: "TruckDispatches");
            migrationBuilder.DropColumn(name: "ToleranceMt", table: "TruckDispatches");
            migrationBuilder.DropColumn(name: "ChargeableShortageMt", table: "TruckDispatches");

            migrationBuilder.DropColumn(name: "TicketSerialNumber", table: "SalesTransactions");
            migrationBuilder.DropColumn(name: "StockSourceType", table: "SalesTransactions");
        }
    }
}
