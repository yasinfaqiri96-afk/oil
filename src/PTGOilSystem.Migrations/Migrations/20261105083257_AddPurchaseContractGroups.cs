using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseContractGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PurchaseContractGroupId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseContractGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ContractDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseContractGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseContractGroups_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_PurchaseContractGroupId",
                table: "Contracts",
                column: "PurchaseContractGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseContractGroups_CompanyId",
                table: "PurchaseContractGroups",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseContractGroups_GroupNumber",
                table: "PurchaseContractGroups",
                column: "GroupNumber",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_PurchaseContractGroups_PurchaseContractGroupId",
                table: "Contracts",
                column: "PurchaseContractGroupId",
                principalTable: "PurchaseContractGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_PurchaseContractGroups_PurchaseContractGroupId",
                table: "Contracts");

            migrationBuilder.DropTable(
                name: "PurchaseContractGroups");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_PurchaseContractGroupId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "PurchaseContractGroupId",
                table: "Contracts");
        }
    }
}
