using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260829191133_AddLedgerSourceDeleteGuard")]
public partial class AddLedgerSourceDeleteGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateGuard(migrationBuilder, "sale", "SalesTransactions", "Sale");
        CreateGuard(migrationBuilder, "expense", "ExpenseTransactions", "Expense");
        CreateGuard(migrationBuilder, "supplier_balance_transfer", "SupplierBalanceTransfers", "SupplierBalanceTransfer");
        CreateGuard(migrationBuilder, "contract_balance_transfer", "ContractBalanceTransfers", "ContractBalanceTransfer");
        CreateGuard(migrationBuilder, "payment", "PaymentTransactions",
            "CustomerReceipt','SupplierPayment','ExpensePayment','TruckPayment','ManualPayment','ManualReceipt','EmployeeSalaryPayment','EmployeeSalaryAdvance','SupplierReceipt','CustomerPayment','EmployeeReturn','ServiceProviderPayment','SarrafSettlement','CommissionPayment");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        DropGuard(migrationBuilder, "sale", "SalesTransactions");
        DropGuard(migrationBuilder, "expense", "ExpenseTransactions");
        DropGuard(migrationBuilder, "supplier_balance_transfer", "SupplierBalanceTransfers");
        DropGuard(migrationBuilder, "contract_balance_transfer", "ContractBalanceTransfers");
        DropGuard(migrationBuilder, "payment", "PaymentTransactions");
    }

    private static void CreateGuard(MigrationBuilder migrationBuilder, string key, string table, string sourceTypes)
        => migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION ptg_guard_{{key}}_ledger_delete()
            RETURNS TRIGGER AS $$
            DECLARE
                remaining INTEGER;
            BEGIN
                SELECT COUNT(*) INTO remaining
                FROM "LedgerEntries" l
                WHERE l."SourceId" = OLD."Id"
                  AND l."SourceType" IN ('{{sourceTypes}}');

                IF remaining > 0 THEN
                    RAISE EXCEPTION
                        'PTG: cannot delete {{table}}#% while % ledger row(s) still reference it. Reverse or remove the ledger rows in the same transaction.',
                        OLD."Id", remaining
                        USING ERRCODE = 'foreign_key_violation';
                END IF;

                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS ptg_guard_{{key}}_ledger_delete ON "{{table}}";
            CREATE CONSTRAINT TRIGGER ptg_guard_{{key}}_ledger_delete
            AFTER DELETE ON "{{table}}"
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION ptg_guard_{{key}}_ledger_delete();
            """);

    private static void DropGuard(MigrationBuilder migrationBuilder, string key, string table)
        => migrationBuilder.Sql($$"""
            DROP TRIGGER IF EXISTS ptg_guard_{{key}}_ledger_delete ON "{{table}}";
            DROP FUNCTION IF EXISTS ptg_guard_{{key}}_ledger_delete();
            """);
}
