using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingReceiptLinkage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoadingReceiptId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoadingReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoadingRegisterId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    ReceiptDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceDocument = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadingReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoadingReceipts_LoadingRegisters_LoadingRegisterId",
                        column: x => x.LoadingRegisterId,
                        principalTable: "LoadingRegisters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceipts_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadingReceipts_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_LoadingReceiptId",
                table: "InventoryMovements",
                column: "LoadingReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceipts_LoadingRegisterId",
                table: "LoadingReceipts",
                column: "LoadingRegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceipts_StorageTankId",
                table: "LoadingReceipts",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceipts_TerminalId",
                table: "LoadingReceipts",
                column: "TerminalId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_LoadingReceipts_LoadingReceiptId",
                table: "InventoryMovements",
                column: "LoadingReceiptId",
                principalTable: "LoadingReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_LoadingReceipts_LoadingReceiptId",
                table: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "LoadingReceipts");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_LoadingReceiptId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "LoadingReceiptId",
                table: "InventoryMovements");
        }
    }
}
