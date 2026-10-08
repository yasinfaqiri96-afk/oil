using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class SplitEmployeeAndAccruedPayableAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Rename only the exact Stage-2 seeded account when it has never
                -- been used by a journal. Historical account meaning is preserved.
                UPDATE "Accounts" AS employee
                SET "Name" = 'Employee Payable',
                    "UpdatedAtUtc" = NOW()
                WHERE employee."Code" = '2500'
                  AND employee."Name" = 'Employee/Accrued Payable'
                  AND EXISTS
                  (
                      SELECT 1
                      FROM "AccountingSettings" AS settings
                      WHERE settings."CompanyId" = employee."CompanyId"
                        AND settings."EmployeePayableAccountId" = employee."Id"
                        AND settings."AccruedExpenseAccountId" = employee."Id"
                  )
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM "JournalEntryLines" AS line
                      WHERE line."AccountId" = employee."Id"
                  );

                -- Add the separate accrued-payable account only for companies whose
                -- settings still point accrued expenses to seeded code 2500.
                INSERT INTO "Accounts"
                (
                    "CompanyId", "Code", "Name", "AccountType", "NormalBalance",
                    "ParentAccountId", "IsControlAccount", "AllowManualPosting", "IsActive",
                    "CreatedAtUtc", "UpdatedAtUtc", "CreatedByUserId", "UpdatedByUserId"
                )
                SELECT
                    settings."CompanyId", '2510', 'Accrued Expenses Payable', 2, 2,
                    NULL, TRUE, FALSE, TRUE,
                    NOW(), NOW(), NULL, NULL
                FROM "AccountingSettings" AS settings
                INNER JOIN "Accounts" AS current_accrued
                    ON current_accrued."Id" = settings."AccruedExpenseAccountId"
                   AND current_accrued."CompanyId" = settings."CompanyId"
                WHERE current_accrued."Code" = '2500'
                ON CONFLICT ("CompanyId", "Code") DO NOTHING;

                -- Future accrued postings use 2510; EmployeePayableAccountId remains
                -- on 2500. Existing journals and account rows are never rewritten.
                UPDATE "AccountingSettings" AS settings
                SET "AccruedExpenseAccountId" = accrued."Id",
                    "UpdatedAtUtc" = NOW()
                FROM "Accounts" AS current_accrued,
                     "Accounts" AS accrued
                WHERE current_accrued."Id" = settings."AccruedExpenseAccountId"
                  AND current_accrued."CompanyId" = settings."CompanyId"
                  AND current_accrued."Code" = '2500'
                  AND accrued."CompanyId" = settings."CompanyId"
                  AND accrued."Code" = '2510';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Conservative rollback: only restore the old mapping/name if 2510
                -- has no journal usage. The 2510 account row is intentionally kept
                -- so a downgrade never deletes a potentially user-managed account.
                UPDATE "AccountingSettings" AS settings
                SET "AccruedExpenseAccountId" = employee."Id",
                    "UpdatedAtUtc" = NOW()
                FROM "Accounts" AS employee,
                     "Accounts" AS accrued
                WHERE settings."EmployeePayableAccountId" = employee."Id"
                  AND settings."AccruedExpenseAccountId" = accrued."Id"
                  AND employee."CompanyId" = settings."CompanyId"
                  AND employee."Code" = '2500'
                  AND accrued."CompanyId" = settings."CompanyId"
                  AND accrued."Code" = '2510'
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM "JournalEntryLines" AS line
                      WHERE line."AccountId" = accrued."Id"
                  );

                UPDATE "Accounts" AS employee
                SET "Name" = 'Employee/Accrued Payable',
                    "UpdatedAtUtc" = NOW()
                WHERE employee."Code" = '2500'
                  AND employee."Name" = 'Employee Payable'
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM "JournalEntryLines" AS line
                      WHERE line."AccountId" = employee."Id"
                  )
                  AND EXISTS
                  (
                      SELECT 1
                      FROM "AccountingSettings" AS settings
                      WHERE settings."EmployeePayableAccountId" = employee."Id"
                        AND settings."AccruedExpenseAccountId" = employee."Id"
                  );
                """);
        }
    }
}
