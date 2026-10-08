using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentLoadingAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShipmentLoadingAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShipmentId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    LoadingRegisterId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentLoadingAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentLoadingAllocations_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLoadingAllocations_LoadingRegisters_LoadingRegister~",
                        column: x => x.LoadingRegisterId,
                        principalTable: "LoadingRegisters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLoadingAllocations_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLoadingAllocations_ContractId",
                table: "ShipmentLoadingAllocations",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLoadingAllocations_LoadingRegisterId",
                table: "ShipmentLoadingAllocations",
                column: "LoadingRegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLoadingAllocations_ShipmentId_LoadingRegisterId",
                table: "ShipmentLoadingAllocations",
                columns: new[] { "ShipmentId", "LoadingRegisterId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShipmentLoadingAllocations");
        }
    }
}
