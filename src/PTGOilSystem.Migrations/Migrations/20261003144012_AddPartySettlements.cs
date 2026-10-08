using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPartySettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartySettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SettlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FromPartyType = table.Column<int>(type: "integer", nullable: false),
                    FromPartyId = table.Column<int>(type: "integer", nullable: false),
                    ToPartyType = table.Column<int>(type: "integer", nullable: false),
                    ToPartyId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CurrencyPerUsdRate = table.Column<decimal>(type: "numeric(24,12)", nullable: true),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(24,12)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FromLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    ToLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySettlements_LedgerEntries_FromLedgerEntryId",
                        column: x => x.FromLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartySettlements_LedgerEntries_ToLedgerEntryId",
                        column: x => x.ToLedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_FromLedgerEntryId",
                table: "PartySettlements",
                column: "FromLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_FromPartyType_FromPartyId",
                table: "PartySettlements",
                columns: new[] { "FromPartyType", "FromPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_SettlementDate",
                table: "PartySettlements",
                column: "SettlementDate");

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_Status",
                table: "PartySettlements",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_ToLedgerEntryId",
                table: "PartySettlements",
                column: "ToLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartySettlements_ToPartyType_ToPartyId",
                table: "PartySettlements",
                columns: new[] { "ToPartyType", "ToPartyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartySettlements");
        }
    }
}
