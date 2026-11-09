using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSuppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "LoadingRegisters",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContractSuppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractSuppliers_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractSuppliers_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoadingRegisters_SupplierId",
                table: "LoadingRegisters",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSuppliers_ContractId_SupplierId",
                table: "ContractSuppliers",
                columns: new[] { "ContractId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractSuppliers_SupplierId",
                table: "ContractSuppliers",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingRegisters_Suppliers_SupplierId",
                table: "LoadingRegisters",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LoadingRegisters_Suppliers_SupplierId",
                table: "LoadingRegisters");

            migrationBuilder.DropTable(
                name: "ContractSuppliers");

            migrationBuilder.DropIndex(
                name: "IX_LoadingRegisters_SupplierId",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "LoadingRegisters");
        }
    }
}
