using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddHrSalaryIntegrityAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GrantedPermissions",
                table: "Roles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalLedgerEntryId",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalPaymentTransactionId",
                table: "EmployeeSalaryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalaryExpenseAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_ReversalLedgerEntryId",
                table: "EmployeeSalaryTransactions",
                column: "ReversalLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryTransactions_ReversalPaymentTransactionId",
                table: "EmployeeSalaryTransactions",
                column: "ReversalPaymentTransactionId",
                unique: true);

            // یک «ثبت معاش» فعال برای هر کارمند در هر ماه. اگر دادهٔ فعلی از قبل تکرار داشته باشد
            // ساختن ایندکس کل Migration را می‌شکند؛ پس فقط وقتی ساخته می‌شود که تکراری نباشد و
            // در غیر این صورت با NOTICE رد می‌شود (قاعدهٔ سرویس همچنان تکرارِ تازه را رد می‌کند).
            // تکرارهای قدیمی باید دستی بررسی و یکی لغو شود؛ هیچ ردیفی خودکار تغییر نمی‌کند.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "EmployeeSalaryTransactions"
                        WHERE "TransactionType" = 1 AND "IsCancelled" = false
                        GROUP BY "EmployeeId", "SalaryPeriodYear", "SalaryPeriodMonth"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE NOTICE 'UX_EmployeeSalaryTransactions_ActiveAccrualPerPeriod skipped: duplicate active salary accruals exist and must be reviewed.';
                    ELSE
                        CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmployeeSalaryTransactions_ActiveAccrualPerPeriod"
                            ON "EmployeeSalaryTransactions" ("EmployeeId", "SalaryPeriodYear", "SalaryPeriodMonth")
                            WHERE "TransactionType" = 1 AND "IsCancelled" = false;
                    END IF;
                END $$;
                """);

            // «کارمندان» از گروه «اشخاص» به گروه تازهٔ «مدیریت بشری» رفت. نقش‌هایی که فهرست
            // صریح دارند و «اشخاص» را داشتند، همان دسترسی را با کلید تازه نگه می‌دارند.
            migrationBuilder.Sql("""
                UPDATE "Roles"
                SET "AllowedNavigationItems" = "AllowedNavigationItems" || ',HumanResources'
                WHERE "AllowedNavigationItems" IS NOT NULL
                  AND (',' || "AllowedNavigationItems" || ',') LIKE '%,Partners,%'
                  AND (',' || "AllowedNavigationItems" || ',') NOT LIKE '%,HumanResources,%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_SalaryExpenseAccountId",
                table: "AccountingSettings",
                column: "SalaryExpenseAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_SalaryExpenseAccountId",
                table: "AccountingSettings",
                column: "SalaryExpenseAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeSalaryTransactions_LedgerEntries_ReversalLedgerEntr~",
                table: "EmployeeSalaryTransactions",
                column: "ReversalLedgerEntryId",
                principalTable: "LedgerEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeSalaryTransactions_PaymentTransactions_ReversalPaym~",
                table: "EmployeeSalaryTransactions",
                column: "ReversalPaymentTransactionId",
                principalTable: "PaymentTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_SalaryExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeSalaryTransactions_LedgerEntries_ReversalLedgerEntr~",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeSalaryTransactions_PaymentTransactions_ReversalPaym~",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeSalaryTransactions_ReversalLedgerEntryId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeSalaryTransactions_ReversalPaymentTransactionId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "UX_EmployeeSalaryTransactions_ActiveAccrualPerPeriod";""");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_SalaryExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "GrantedPermissions",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "ReversalLedgerEntryId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalPaymentTransactionId",
                table: "EmployeeSalaryTransactions");

            migrationBuilder.DropColumn(
                name: "SalaryExpenseAccountId",
                table: "AccountingSettings");
        }
    }
}
