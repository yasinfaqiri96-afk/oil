using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations;

/// <summary>
/// PaymentKindهای قرضهٔ کارمند (EmployeeLoan, EmployeeLoanRepayment) بعد از
/// AddLedgerSourceDeleteGuard اضافه شدند؛ محافظِ حذفِ پرداخت آن‌ها را هم پوشش می‌دهد.
/// فقط بدنهٔ تابع عوض می‌شود؛ تریگر و داده دست نمی‌خورند.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260930120000_ExtendPaymentLedgerDeleteGuardForEmployeeLoans")]
public partial class ExtendPaymentLedgerDeleteGuardForEmployeeLoans : Migration
{
    private const string PreviousSourceTypes =
        "CustomerReceipt','SupplierPayment','ExpensePayment','TruckPayment','ManualPayment','ManualReceipt','EmployeeSalaryPayment','EmployeeSalaryAdvance','SupplierReceipt','CustomerPayment','EmployeeReturn','ServiceProviderPayment','SarrafSettlement','CommissionPayment";

    protected override void Up(MigrationBuilder migrationBuilder)
        => ReplacePaymentGuardFunction(migrationBuilder, PreviousSourceTypes + "','EmployeeLoan','EmployeeLoanRepayment");

    protected override void Down(MigrationBuilder migrationBuilder)
        => ReplacePaymentGuardFunction(migrationBuilder, PreviousSourceTypes);

    private static void ReplacePaymentGuardFunction(MigrationBuilder migrationBuilder, string sourceTypes)
        => migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION ptg_guard_payment_ledger_delete()
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
                        'PTG: cannot delete PaymentTransactions#% while % ledger row(s) still reference it. Reverse or remove the ledger rows in the same transaction.',
                        OLD."Id", remaining
                        USING ERRCODE = 'foreign_key_violation';
                END IF;

                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;
            """);
}
