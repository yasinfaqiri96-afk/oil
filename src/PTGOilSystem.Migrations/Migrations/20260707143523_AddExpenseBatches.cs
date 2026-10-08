using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpenseBatchId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpenseTypeId = table.Column<int>(type: "integer", nullable: false),
                    ServiceProviderId = table.Column<int>(type: "integer", nullable: true),
                    ExpenseDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllocationMethod = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppliedFxRateToUsd = table.Column<decimal>(type: "numeric", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalAmountUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    OperationCount = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseBatches_ExpenseTypes_ExpenseTypeId",
                        column: x => x.ExpenseTypeId,
                        principalTable: "ExpenseTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseBatches_ServiceProviders_ServiceProviderId",
                        column: x => x.ServiceProviderId,
                        principalTable: "ServiceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_ExpenseBatchId",
                table: "ExpenseTransactions",
                column: "ExpenseBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseBatches_BatchNumber",
                table: "ExpenseBatches",
                column: "BatchNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseBatches_ExpenseTypeId",
                table: "ExpenseBatches",
                column: "ExpenseTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseBatches_ServiceProviderId",
                table: "ExpenseBatches",
                column: "ServiceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_ExpenseBatches_ExpenseBatchId",
                table: "ExpenseTransactions",
                column: "ExpenseBatchId",
                principalTable: "ExpenseBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_ExpenseBatches_ExpenseBatchId",
                table: "ExpenseTransactions");

            migrationBuilder.DropTable(
                name: "ExpenseBatches");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_ExpenseBatchId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "ExpenseBatchId",
                table: "ExpenseTransactions");
        }
    }
}
