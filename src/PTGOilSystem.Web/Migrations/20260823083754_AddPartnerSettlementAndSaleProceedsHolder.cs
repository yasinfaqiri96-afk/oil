using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerSettlementAndSaleProceedsHolder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SaleProceedsHolderPartnerId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PartnerSettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SettlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FromPartnerId = table.Column<int>(type: "integer", nullable: false),
                    ToPartnerId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppliedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsReversed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartnerSettlements_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartnerSettlements_Partners_FromPartnerId",
                        column: x => x.FromPartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartnerSettlements_Partners_ToPartnerId",
                        column: x => x.ToPartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_SaleProceedsHolderPartnerId",
                table: "Contracts",
                column: "SaleProceedsHolderPartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSettlements_ContractId",
                table: "PartnerSettlements",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSettlements_FromPartnerId_ToPartnerId_SettlementDate",
                table: "PartnerSettlements",
                columns: new[] { "FromPartnerId", "ToPartnerId", "SettlementDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSettlements_ToPartnerId",
                table: "PartnerSettlements",
                column: "ToPartnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Partners_SaleProceedsHolderPartnerId",
                table: "Contracts",
                column: "SaleProceedsHolderPartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Partners_SaleProceedsHolderPartnerId",
                table: "Contracts");

            migrationBuilder.DropTable(
                name: "PartnerSettlements");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_SaleProceedsHolderPartnerId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SaleProceedsHolderPartnerId",
                table: "Contracts");
        }
    }
}
