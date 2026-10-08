using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "LedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderType = table.Column<int>(type: "integer", nullable: false),
                    Country = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TaxNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceProviders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ServiceProviderId",
                table: "PaymentTransactions",
                column: "ServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ServiceProviderId",
                table: "LedgerEntries",
                column: "ServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_ServiceProviderId",
                table: "ExpenseTransactions",
                column: "ServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceProviders_Code",
                table: "ServiceProviders",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceProviders_IsActive_Name",
                table: "ServiceProviders",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_ServiceProviders_ServiceProviderId",
                table: "ExpenseTransactions",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_ServiceProviders_ServiceProviderId",
                table: "LedgerEntries",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_ServiceProviders_ServiceProviderId",
                table: "PaymentTransactions",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_ServiceProviders_ServiceProviderId",
                table: "ExpenseTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_ServiceProviders_ServiceProviderId",
                table: "LedgerEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_ServiceProviders_ServiceProviderId",
                table: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "ServiceProviders");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_ServiceProviderId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_ServiceProviderId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_ServiceProviderId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "ExpenseTransactions");
        }
    }
}
