using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingCoreAndFiscalCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountType = table.Column<int>(type: "integer", nullable: false),
                    NormalBalance = table.Column<int>(type: "integer", nullable: false),
                    ParentAccountId = table.Column<int>(type: "integer", nullable: true),
                    IsControlAccount = table.Column<bool>(type: "boolean", nullable: false),
                    AllowManualPosting = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Accounts_Accounts_ParentAccountId",
                        column: x => x.ParentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FunctionalCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CashBankControlAccountId = table.Column<int>(type: "integer", nullable: false),
                    AccountsReceivableAccountId = table.Column<int>(type: "integer", nullable: false),
                    AccountsPayableAccountId = table.Column<int>(type: "integer", nullable: false),
                    InventoryAccountId = table.Column<int>(type: "integer", nullable: false),
                    InventoryInTransitAccountId = table.Column<int>(type: "integer", nullable: false),
                    SupplierPrepaymentAccountId = table.Column<int>(type: "integer", nullable: false),
                    CustomerAdvanceAccountId = table.Column<int>(type: "integer", nullable: false),
                    FreightPayableAccountId = table.Column<int>(type: "integer", nullable: false),
                    CommissionPayableAccountId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeAdvanceAccountId = table.Column<int>(type: "integer", nullable: false),
                    EmployeePayableAccountId = table.Column<int>(type: "integer", nullable: false),
                    AccruedExpenseAccountId = table.Column<int>(type: "integer", nullable: false),
                    SalesRevenueAccountId = table.Column<int>(type: "integer", nullable: false),
                    CostOfGoodsSoldAccountId = table.Column<int>(type: "integer", nullable: false),
                    GeneralExpenseAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExchangeGainAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExchangeLossAccountId = table.Column<int>(type: "integer", nullable: false),
                    InventoryLossAccountId = table.Column<int>(type: "integer", nullable: false),
                    CurrentYearProfitLossAccountId = table.Column<int>(type: "integer", nullable: false),
                    RetainedEarningsAccountId = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_AccountsPayableAccountId",
                        column: x => x.AccountsPayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_AccountsReceivableAccountId",
                        column: x => x.AccountsReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_AccruedExpenseAccountId",
                        column: x => x.AccruedExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_CashBankControlAccountId",
                        column: x => x.CashBankControlAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_CommissionPayableAccountId",
                        column: x => x.CommissionPayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_CostOfGoodsSoldAccountId",
                        column: x => x.CostOfGoodsSoldAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_CurrentYearProfitLossAccountId",
                        column: x => x.CurrentYearProfitLossAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_CustomerAdvanceAccountId",
                        column: x => x.CustomerAdvanceAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_EmployeeAdvanceAccountId",
                        column: x => x.EmployeeAdvanceAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_EmployeePayableAccountId",
                        column: x => x.EmployeePayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_ExchangeGainAccountId",
                        column: x => x.ExchangeGainAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_ExchangeLossAccountId",
                        column: x => x.ExchangeLossAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_FreightPayableAccountId",
                        column: x => x.FreightPayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_GeneralExpenseAccountId",
                        column: x => x.GeneralExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_InventoryAccountId",
                        column: x => x.InventoryAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_InventoryInTransitAccountId",
                        column: x => x.InventoryInTransitAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_InventoryLossAccountId",
                        column: x => x.InventoryLossAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_RetainedEarningsAccountId",
                        column: x => x.RetainedEarningsAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_SalesRevenueAccountId",
                        column: x => x.SalesRevenueAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Accounts_SupplierPrepaymentAccountId",
                        column: x => x.SupplierPrepaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingSettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FiscalYearId = table.Column<int>(type: "integer", nullable: false),
                    PeriodNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: false),
                    EndDate = table.Column<DateTime>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalPeriods", x => x.Id);
                    table.CheckConstraint("CK_FiscalPeriods_DateRange", "\"StartDate\" <= \"EndDate\"");
                    table.CheckConstraint("CK_FiscalPeriods_PeriodNumber", "\"PeriodNumber\" BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_FiscalPeriods_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalPeriods_Users_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalYearCloseRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FiscalYearId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ClosingJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    OpeningJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalYearCloseRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalYearCloseRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYearCloseRuns_Users_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYearCloseRuns_Users_StartedByUserId",
                        column: x => x.StartedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalYears",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: false),
                    EndDate = table.Column<DateTime>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PreviousFiscalYearId = table.Column<int>(type: "integer", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    OpeningJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    ClosingJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OpenedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalYears", x => x.Id);
                    table.CheckConstraint("CK_FiscalYears_DateRange", "\"StartDate\" <= \"EndDate\"");
                    table.ForeignKey(
                        name: "FK_FiscalYears_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYears_FiscalYears_PreviousFiscalYearId",
                        column: x => x.PreviousFiscalYearId,
                        principalTable: "FiscalYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYears_Users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYears_Users_OpenedByUserId",
                        column: x => x.OpenedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalYearStatusHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FiscalYearId = table.Column<int>(type: "integer", nullable: false),
                    OldStatus = table.Column<int>(type: "integer", nullable: false),
                    NewStatus = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalYearStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalYearStatusHistories_FiscalYears_FiscalYearId",
                        column: x => x.FiscalYearId,
                        principalTable: "FiscalYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYearStatusHistories_Users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FiscalYearId = table.Column<int>(type: "integer", nullable: false),
                    FiscalPeriodId = table.Column<int>(type: "integer", nullable: false),
                    JournalNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AccountingDate = table.Column<DateTime>(type: "date", nullable: false),
                    DocumentDate = table.Column<DateTime>(type: "date", nullable: false),
                    OperationDate = table.Column<DateTime>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceModule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceEntityId = table.Column<int>(type: "integer", nullable: true),
                    SourceEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsOpening = table.Column<bool>(type: "boolean", nullable: false),
                    IsClosing = table.Column<bool>(type: "boolean", nullable: false),
                    IsAdjustment = table.Column<bool>(type: "boolean", nullable: false),
                    IsReversal = table.Column<bool>(type: "boolean", nullable: false),
                    ReversalOfJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedByUserId = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.CheckConstraint("CK_JournalEntries_ReversalReference", "(\"IsReversal\" = TRUE AND \"ReversalOfJournalEntryId\" IS NOT NULL) OR (\"IsReversal\" = FALSE AND \"ReversalOfJournalEntryId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_JournalEntries_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_FiscalPeriods_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_FiscalYears_FiscalYearId",
                        column: x => x.FiscalYearId,
                        principalTable: "FiscalYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_JournalEntries_ReversalOfJournalEntryId",
                        column: x => x.ReversalOfJournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Users_PostedByUserId",
                        column: x => x.PostedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntryLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    PartyType = table.Column<int>(type: "integer", nullable: true),
                    PartyId = table.Column<int>(type: "integer", nullable: true),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    ShipmentId = table.Column<int>(type: "integer", nullable: true),
                    TankId = table.Column<int>(type: "integer", nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    CashAccountId = table.Column<int>(type: "integer", nullable: true),
                    Debit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TransactionCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TransactionAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryLines", x => x.Id);
                    table.CheckConstraint("CK_JournalEntryLines_DebitCredit", "\"Debit\" >= 0 AND \"Credit\" >= 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Credit\" > 0 AND \"Debit\" = 0))");
                    table.CheckConstraint("CK_JournalEntryLines_Party", "(\"PartyType\" IS NULL AND \"PartyId\" IS NULL) OR (\"PartyType\" IS NOT NULL AND \"PartyId\" IS NOT NULL)");
                    table.CheckConstraint("CK_JournalEntryLines_Transaction", "\"TransactionAmount\" >= 0 AND \"ExchangeRate\" > 0");
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_StorageTanks_TankId",
                        column: x => x.TankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AccountsPayableAccountId",
                table: "AccountingSettings",
                column: "AccountsPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AccountsReceivableAccountId",
                table: "AccountingSettings",
                column: "AccountsReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AccruedExpenseAccountId",
                table: "AccountingSettings",
                column: "AccruedExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CashBankControlAccountId",
                table: "AccountingSettings",
                column: "CashBankControlAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CommissionPayableAccountId",
                table: "AccountingSettings",
                column: "CommissionPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CompanyId",
                table: "AccountingSettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CostOfGoodsSoldAccountId",
                table: "AccountingSettings",
                column: "CostOfGoodsSoldAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CurrentYearProfitLossAccountId",
                table: "AccountingSettings",
                column: "CurrentYearProfitLossAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CustomerAdvanceAccountId",
                table: "AccountingSettings",
                column: "CustomerAdvanceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_EmployeeAdvanceAccountId",
                table: "AccountingSettings",
                column: "EmployeeAdvanceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_EmployeePayableAccountId",
                table: "AccountingSettings",
                column: "EmployeePayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_ExchangeGainAccountId",
                table: "AccountingSettings",
                column: "ExchangeGainAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_ExchangeLossAccountId",
                table: "AccountingSettings",
                column: "ExchangeLossAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_FreightPayableAccountId",
                table: "AccountingSettings",
                column: "FreightPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_GeneralExpenseAccountId",
                table: "AccountingSettings",
                column: "GeneralExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_InventoryAccountId",
                table: "AccountingSettings",
                column: "InventoryAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_InventoryInTransitAccountId",
                table: "AccountingSettings",
                column: "InventoryInTransitAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_InventoryLossAccountId",
                table: "AccountingSettings",
                column: "InventoryLossAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_RetainedEarningsAccountId",
                table: "AccountingSettings",
                column: "RetainedEarningsAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_SalesRevenueAccountId",
                table: "AccountingSettings",
                column: "SalesRevenueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_SupplierPrepaymentAccountId",
                table: "AccountingSettings",
                column: "SupplierPrepaymentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CompanyId",
                table: "Accounts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CompanyId_Code",
                table: "Accounts",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CompanyId_IsActive",
                table: "Accounts",
                columns: new[] { "CompanyId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ParentAccountId",
                table: "Accounts",
                column: "ParentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriods_CompanyId",
                table: "FiscalPeriods",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriods_CompanyId_Status",
                table: "FiscalPeriods",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriods_FiscalYearId",
                table: "FiscalPeriods",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriods_FiscalYearId_PeriodNumber",
                table: "FiscalPeriods",
                columns: new[] { "FiscalYearId", "PeriodNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriods_LockedByUserId",
                table: "FiscalPeriods",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_ClosingJournalEntryId",
                table: "FiscalYearCloseRuns",
                column: "ClosingJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_CompanyId",
                table: "FiscalYearCloseRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_CompletedByUserId",
                table: "FiscalYearCloseRuns",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_FiscalYearId",
                table: "FiscalYearCloseRuns",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_FiscalYearId_Status",
                table: "FiscalYearCloseRuns",
                columns: new[] { "FiscalYearId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_OpeningJournalEntryId",
                table: "FiscalYearCloseRuns",
                column: "OpeningJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearCloseRuns_StartedByUserId",
                table: "FiscalYearCloseRuns",
                column: "StartedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_ClosedByUserId",
                table: "FiscalYears",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_ClosingJournalEntryId",
                table: "FiscalYears",
                column: "ClosingJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_CompanyId",
                table: "FiscalYears",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_CompanyId_IsCurrent",
                table: "FiscalYears",
                columns: new[] { "CompanyId", "IsCurrent" },
                unique: true,
                filter: "\"IsCurrent\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_CompanyId_Status",
                table: "FiscalYears",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_OpenedByUserId",
                table: "FiscalYears",
                column: "OpenedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_OpeningJournalEntryId",
                table: "FiscalYears",
                column: "OpeningJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_PreviousFiscalYearId",
                table: "FiscalYears",
                column: "PreviousFiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearStatusHistories_ChangedByUserId",
                table: "FiscalYearStatusHistories",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYearStatusHistories_FiscalYearId_ChangedAt",
                table: "FiscalYearStatusHistories",
                columns: new[] { "FiscalYearId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_AccountingDate",
                table: "JournalEntries",
                column: "AccountingDate");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CompanyId",
                table: "JournalEntries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CompanyId_JournalNumber",
                table: "JournalEntries",
                columns: new[] { "CompanyId", "JournalNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CompanyId_SourceModule_SourceEventId",
                table: "JournalEntries",
                columns: new[] { "CompanyId", "SourceModule", "SourceEventId" },
                unique: true,
                filter: "\"SourceEventId\" IS NOT NULL AND \"SourceEventId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_FiscalPeriodId",
                table: "JournalEntries",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_FiscalYearId",
                table: "JournalEntries",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_PostedByUserId",
                table: "JournalEntries",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversalOfJournalEntryId",
                table: "JournalEntries",
                column: "ReversalOfJournalEntryId",
                unique: true,
                filter: "\"ReversalOfJournalEntryId\" IS NOT NULL AND \"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_SourceEventId",
                table: "JournalEntries",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_AccountId",
                table: "JournalEntryLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_CashAccountId",
                table: "JournalEntryLines",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_ContractId",
                table: "JournalEntryLines",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_JournalEntryId_LineNumber",
                table: "JournalEntryLines",
                columns: new[] { "JournalEntryId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_ProductId",
                table: "JournalEntryLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_ShipmentId",
                table: "JournalEntryLines",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_TankId",
                table: "JournalEntryLines",
                column: "TankId");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalPeriods_FiscalYears_FiscalYearId",
                table: "FiscalPeriods",
                column: "FiscalYearId",
                principalTable: "FiscalYears",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYearCloseRuns_FiscalYears_FiscalYearId",
                table: "FiscalYearCloseRuns",
                column: "FiscalYearId",
                principalTable: "FiscalYears",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYearCloseRuns_JournalEntries_ClosingJournalEntryId",
                table: "FiscalYearCloseRuns",
                column: "ClosingJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYearCloseRuns_JournalEntries_OpeningJournalEntryId",
                table: "FiscalYearCloseRuns",
                column: "OpeningJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYears_JournalEntries_ClosingJournalEntryId",
                table: "FiscalYears",
                column: "ClosingJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYears_JournalEntries_OpeningJournalEntryId",
                table: "FiscalYears",
                column: "OpeningJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS btree_gist;

                ALTER TABLE "FiscalYears"
                ADD CONSTRAINT "EX_FiscalYears_NoOverlap"
                EXCLUDE USING gist
                (
                    "CompanyId" WITH =,
                    daterange("StartDate", "EndDate", '[]') WITH &&
                );
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ptg_validate_fiscal_period()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    fiscal_year_company integer;
                    fiscal_year_start date;
                    fiscal_year_end date;
                BEGIN
                    SELECT "CompanyId", "StartDate", "EndDate"
                    INTO fiscal_year_company, fiscal_year_start, fiscal_year_end
                    FROM "FiscalYears"
                    WHERE "Id" = NEW."FiscalYearId";

                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'Fiscal year % does not exist.', NEW."FiscalYearId"
                            USING ERRCODE = '23503';
                    END IF;

                    IF NEW."CompanyId" <> fiscal_year_company THEN
                        RAISE EXCEPTION 'Fiscal period company must match fiscal year company.'
                            USING ERRCODE = '23514';
                    END IF;

                    IF NEW."StartDate" < fiscal_year_start OR NEW."EndDate" > fiscal_year_end THEN
                        RAISE EXCEPTION 'Fiscal period dates must be inside the fiscal year.'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_FiscalPeriods_ValidateRange"
                BEFORE INSERT OR UPDATE OF "CompanyId", "FiscalYearId", "StartDate", "EndDate"
                ON "FiscalPeriods"
                FOR EACH ROW
                EXECUTE FUNCTION ptg_validate_fiscal_period();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ptg_validate_journal_posting()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    fiscal_year_company integer;
                    fiscal_year_start date;
                    fiscal_year_end date;
                    fiscal_year_status integer;
                    fiscal_period_company integer;
                    fiscal_period_year integer;
                    fiscal_period_start date;
                    fiscal_period_end date;
                    fiscal_period_status integer;
                    line_count integer;
                    total_debit numeric(18,4);
                    total_credit numeric(18,4);
                BEGIN
                    SELECT "CompanyId", "StartDate", "EndDate", "Status"
                    INTO fiscal_year_company, fiscal_year_start, fiscal_year_end, fiscal_year_status
                    FROM "FiscalYears"
                    WHERE "Id" = NEW."FiscalYearId";

                    SELECT "CompanyId", "FiscalYearId", "StartDate", "EndDate", "Status"
                    INTO fiscal_period_company, fiscal_period_year, fiscal_period_start, fiscal_period_end, fiscal_period_status
                    FROM "FiscalPeriods"
                    WHERE "Id" = NEW."FiscalPeriodId";

                    IF NEW."CompanyId" <> fiscal_year_company
                        OR NEW."CompanyId" <> fiscal_period_company
                        OR NEW."FiscalYearId" <> fiscal_period_year THEN
                        RAISE EXCEPTION 'Journal company, fiscal year, and fiscal period do not match.'
                            USING ERRCODE = '23514';
                    END IF;

                    IF NEW."AccountingDate" < fiscal_year_start
                        OR NEW."AccountingDate" > fiscal_year_end
                        OR NEW."AccountingDate" < fiscal_period_start
                        OR NEW."AccountingDate" > fiscal_period_end THEN
                        RAISE EXCEPTION 'Accounting date is outside the selected fiscal year or period.'
                            USING ERRCODE = '23514';
                    END IF;

                    -- Draft journals can be built gradually. Balance and open-calendar
                    -- checks run only when the journal becomes officially posted.
                    IF NEW."Status" = 1 THEN
                        IF fiscal_year_status <> 1 OR fiscal_period_status <> 1 THEN
                            RAISE EXCEPTION 'Posting requires an open fiscal year and open fiscal period.'
                                USING ERRCODE = '23514';
                        END IF;

                        SELECT COUNT(*), COALESCE(SUM("Debit"), 0), COALESCE(SUM("Credit"), 0)
                        INTO line_count, total_debit, total_credit
                        FROM "JournalEntryLines"
                        WHERE "JournalEntryId" = NEW."Id";

                        IF line_count < 2 OR total_debit <= 0 OR total_debit <> total_credit THEN
                            RAISE EXCEPTION 'Posted journal must have at least two balanced lines.'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_JournalEntries_ValidatePosting"
                BEFORE INSERT OR UPDATE OF "Status", "AccountingDate", "CompanyId", "FiscalYearId", "FiscalPeriodId"
                ON "JournalEntries"
                FOR EACH ROW
                EXECUTE FUNCTION ptg_validate_journal_posting();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ptg_protect_posted_journal()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF OLD."Status" = 1 THEN
                        RAISE EXCEPTION 'Posted journals are immutable; create a reversal instead.'
                            USING ERRCODE = '23514';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_JournalEntries_ProtectPosted"
                BEFORE UPDATE OR DELETE
                ON "JournalEntries"
                FOR EACH ROW
                EXECUTE FUNCTION ptg_protect_posted_journal();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ptg_protect_and_validate_journal_line()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    old_journal_status integer;
                    new_journal_status integer;
                    journal_company integer;
                    account_company integer;
                BEGIN
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        SELECT "Status"
                        INTO old_journal_status
                        FROM "JournalEntries"
                        WHERE "Id" = OLD."JournalEntryId";

                        IF old_journal_status = 1 THEN
                            RAISE EXCEPTION 'Lines of a posted journal are immutable.'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;

                    IF TG_OP IN ('INSERT', 'UPDATE') THEN
                        SELECT "Status", "CompanyId"
                        INTO new_journal_status, journal_company
                        FROM "JournalEntries"
                        WHERE "Id" = NEW."JournalEntryId";

                        IF new_journal_status = 1 THEN
                            RAISE EXCEPTION 'Lines cannot be added to or moved into a posted journal.'
                                USING ERRCODE = '23514';
                        END IF;

                        SELECT "CompanyId"
                        INTO account_company
                        FROM "Accounts"
                        WHERE "Id" = NEW."AccountId";

                        IF account_company <> journal_company THEN
                            RAISE EXCEPTION 'Journal line account must belong to the journal company.'
                                USING ERRCODE = '23514';
                        END IF;

                        RETURN NEW;
                    END IF;

                    RETURN OLD;
                END;
                $function$;

                CREATE TRIGGER "TR_JournalEntryLines_ProtectAndValidate"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "JournalEntryLines"
                FOR EACH ROW
                EXECUTE FUNCTION ptg_protect_and_validate_journal_line();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_JournalEntryLines_ProtectAndValidate" ON "JournalEntryLines";
                DROP FUNCTION IF EXISTS ptg_protect_and_validate_journal_line();
                DROP TRIGGER IF EXISTS "TR_JournalEntries_ProtectPosted" ON "JournalEntries";
                DROP FUNCTION IF EXISTS ptg_protect_posted_journal();
                DROP TRIGGER IF EXISTS "TR_JournalEntries_ValidatePosting" ON "JournalEntries";
                DROP FUNCTION IF EXISTS ptg_validate_journal_posting();
                DROP TRIGGER IF EXISTS "TR_FiscalPeriods_ValidateRange" ON "FiscalPeriods";
                DROP FUNCTION IF EXISTS ptg_validate_fiscal_period();
                ALTER TABLE "FiscalYears" DROP CONSTRAINT IF EXISTS "EX_FiscalYears_NoOverlap";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_FiscalPeriods_FiscalYears_FiscalYearId",
                table: "FiscalPeriods");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_FiscalYears_FiscalYearId",
                table: "JournalEntries");

            migrationBuilder.DropTable(
                name: "AccountingSettings");

            migrationBuilder.DropTable(
                name: "FiscalYearCloseRuns");

            migrationBuilder.DropTable(
                name: "FiscalYearStatusHistories");

            migrationBuilder.DropTable(
                name: "JournalEntryLines");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "FiscalYears");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "FiscalPeriods");
        }
    }
}
