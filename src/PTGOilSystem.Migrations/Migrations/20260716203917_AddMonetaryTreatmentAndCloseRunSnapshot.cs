using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMonetaryTreatmentAndCloseRunSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChecklistSnapshotJson",
                table: "FiscalYearCloseRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosingRateSnapshotJson",
                table: "FiscalYearCloseRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditTotal",
                table: "FiscalYearCloseRuns",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DebitTotal",
                table: "FiscalYearCloseRuns",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "FiscalYearCloseRuns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JournalCount",
                table: "FiscalYearCloseRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastJournalEntryId",
                table: "FiscalYearCloseRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastJournalPostedAt",
                table: "FiscalYearCloseRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevaluationJournalIdsJson",
                table: "FiscalYearCloseRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "FiscalYearCloseRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RunType",
                table: "FiscalYearCloseRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotHash",
                table: "FiscalYearCloseRuns",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceDataCutoff",
                table: "FiscalYearCloseRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarningAcknowledgementsJson",
                table: "FiscalYearCloseRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonetaryTreatment",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChecklistSnapshotJson",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "ClosingRateSnapshotJson",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "CreditTotal",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "DebitTotal",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "JournalCount",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "LastJournalEntryId",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "LastJournalPostedAt",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "RevaluationJournalIdsJson",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "RunType",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "SnapshotHash",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "SourceDataCutoff",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "WarningAcknowledgementsJson",
                table: "FiscalYearCloseRuns");

            migrationBuilder.DropColumn(
                name: "MonetaryTreatment",
                table: "Accounts");
        }
    }
}
