using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddHrPayrollAndLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmployeeLoanId",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PayrollRunLineId",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryMonth",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryYear",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeLoans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    LoanDate = table.Column<DateTime>(type: "date", nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    InstallmentCount = table.Column<int>(type: "integer", nullable: false),
                    InstallmentAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StartRecoveryYear = table.Column<int>(type: "integer", nullable: false),
                    StartRecoveryMonth = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DisbursementTransactionId = table.Column<int>(type: "integer", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeLoans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeLoans_EmployeeSalaryTransactions_DisbursementTransa~",
                        column: x => x.DisbursementTransactionId,
                        principalTable: "EmployeeSalaryTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeLoans_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    FinalizedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalizedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ReopenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReopenReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRunLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollRunId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    EmployedDays = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    DailyRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AbsentDays = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    UnpaidLeaveDays = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    LateMinutes = table.Column<int>(type: "integer", nullable: false),
                    BaseSalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OvertimeAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    BonusAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AllowanceAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OtherEarning = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AbsenceDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LateDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnpaidLeaveDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LoanDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AdvanceDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OtherDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    GrossSalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalDeduction = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    NetSalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Warning = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRunLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRunLines_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayrollRunLines_PayrollRuns_PayrollRunId",
                        column: x => x.PayrollRunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_EmployeeLoanId",
                table: "EmployeeSalaryTransactions",
                column: "EmployeeLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_PayrollRunLineId",
                table: "EmployeeSalaryTransactions",
                column: "PayrollRunLineId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLoans_DisbursementTransactionId",
                table: "EmployeeLoans",
                column: "DisbursementTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLoans_EmployeeId_Status",
                table: "EmployeeLoans",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLines_EmployeeId",
                table: "PayrollRunLines",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLines_PayrollRunId_EmployeeId",
                table: "PayrollRunLines",
                columns: new[] { "PayrollRunId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_Status",
                table: "PayrollRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_Year_Month",
                table: "PayrollRuns",
                columns: new[] { "Year", "Month" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeSalaryTransactions_EmployeeLoans_EmployeeLoanId",
                table: "EmployeeSalaryTransactions",
                column: "EmployeeLoanId",
                principalTable: "EmployeeLoans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeSalaryTransactions_PayrollRunLines_PayrollRunLineId",
                table: "EmployeeSalaryTransactions",
                column: "PayrollRunLineId",
                principalTable: "PayrollRunLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeSalaryTransactions_EmployeeLoans_EmployeeLoanId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeSalaryTransactions_PayrollRunLines_PayrollRunLineId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropTable(
                name: "EmployeeLoans");

            migrationBuilder.DropTable(
                name: "PayrollRunLines");

            migrationBuilder.DropTable(
                name: "PayrollRuns");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeSalaryTransactions_EmployeeLoanId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeSalaryTransactions_PayrollRunLineId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "EmployeeLoanId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "PayrollRunLineId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "RecoveryMonth",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "RecoveryYear",
                table: "EmployeeSalaryTransactions");
        }
    }
}
