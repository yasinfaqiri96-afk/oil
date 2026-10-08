using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeesModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmployeeId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmployeeId",
                table: "LedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FatherName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NationalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    JobTitle = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Department = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    EmployeeType = table.Column<int>(type: "integer", nullable: false),
                    SalaryType = table.Column<int>(type: "integer", nullable: false),
                    BaseSalaryAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SalaryCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HireDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeSalaryTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TransactionType = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppliedFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CashAccountId = table.Column<int>(type: "integer", nullable: true),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SalaryPeriodYear = table.Column<int>(type: "integer", nullable: true),
                    SalaryPeriodMonth = table.Column<int>(type: "integer", nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeSalaryTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryTransactions_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryTransactions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryTransactions_LedgerEntries_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryTransactions_PaymentTransactions_PaymentTrans~",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_EmployeeId",
                table: "PaymentTransactions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_EmployeeId",
                table: "LedgerEntries",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Department",
                table: "Employees",
                column: "Department");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeCode",
                table: "Employees",
                column: "EmployeeCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeType",
                table: "Employees",
                column: "EmployeeType");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_FullName",
                table: "Employees",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_IsActive",
                table: "Employees",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_SalaryCurrency",
                table: "Employees",
                column: "SalaryCurrency");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_CashAccountId",
                table: "EmployeeSalaryTransactions",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_EmployeeId_TransactionDate",
                table: "EmployeeSalaryTransactions",
                columns: new[] { "EmployeeId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_IsCancelled",
                table: "EmployeeSalaryTransactions",
                column: "IsCancelled");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_LedgerEntryId",
                table: "EmployeeSalaryTransactions",
                column: "LedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_PaymentTransactionId",
                table: "EmployeeSalaryTransactions",
                column: "PaymentTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_SalaryPeriodYear_SalaryPeriodMon~",
                table: "EmployeeSalaryTransactions",
                columns: new[] { "SalaryPeriodYear", "SalaryPeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_TransactionType",
                table: "EmployeeSalaryTransactions",
                column: "TransactionType");

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_Employees_EmployeeId",
                table: "LedgerEntries",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_Employees_EmployeeId",
                table: "PaymentTransactions",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_Employees_EmployeeId",
                table: "LedgerEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_Employees_EmployeeId",
                table: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "EmployeeSalaryTransactions");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_EmployeeId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_EmployeeId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "LedgerEntries");
        }
    }
}
