using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <summary>
    /// PTG-P1-04 — پرکردنِ هویتِ تسویه برای ردیف‌های پیش از فاز ۱، فقط جایی که رابطه
    /// قطعی است و حدسی در کار نیست.
    ///
    /// دو حالتِ قطعی: <c>ServiceProviderId</c> پر است ⇒ بدهی به همان شرکت خدماتی؛ وگرنه
    /// <c>DriverId</c> پر است ⇒ بدهی به همان راننده. همین ترتیب را
    /// <c>ExpenseAccountingAdapter.ResolveParty</c> و <c>ExpenseLedgerPoster.ResolveSide</c>
    /// از قبل برای سطرِ دفتر کل به‌کار می‌بردند، پس این backfill رفتار مالی را عوض نمی‌کند؛
    /// فقط همان چیزی را که دفتر کل امروز می‌گوید، روی خودِ سند صریح می‌نویسد.
    ///
    /// ردیف‌های بی‌طرف‌حساب عمداً روی <c>Unknown</c> می‌مانند: نبودِ طرف‌حساب می‌تواند
    /// «تعدیلِ داخلی» باشد یا «پرداختِ نقدی که حسابش ثبت نشده»، و تفاوتشان از داده قابل
    /// استنتاج نیست. حدس‌زدن یعنی ساختنِ بدهی یا پرداختی که هیچ‌وقت وجود نداشته، پس
    /// گزارش‌ها آن‌ها را زیر ردیفِ «طبقه‌بندی‌نشده» جدا نشان می‌دهند.
    ///
    /// هیچ <c>LedgerEntry</c> یا سندی ساخته، حذف یا جابه‌جا نمی‌شود؛ این migration فقط
    /// ستون‌های توصیفیِ همان جدول را می‌نویسد و برگشت‌پذیر است.
    /// </summary>
    public partial class BackfillExpenseSettlementCounterparty : Migration
    {
        // AccountingPartyType.ServiceProvider = 3، AccountingPartyType.Driver = 5،
        // ExpenseSettlementMode.Payable = 1، ExpenseSettlementMode.Unknown = 0.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // شرکت خدماتی اولویت دارد — همان ترتیبِ ResolveParty.
            migrationBuilder.Sql(@"
                UPDATE ""ExpenseTransactions""
                SET ""SettlementMode"" = 1,
                    ""CounterpartyType"" = 3,
                    ""CounterpartyId"" = ""ServiceProviderId""
                WHERE ""SettlementMode"" = 0
                  AND ""ServiceProviderId"" IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""ExpenseTransactions""
                SET ""SettlementMode"" = 1,
                    ""CounterpartyType"" = 5,
                    ""CounterpartyId"" = ""DriverId""
                WHERE ""SettlementMode"" = 0
                  AND ""ServiceProviderId"" IS NULL
                  AND ""DriverId"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // فقط ردیف‌هایی که هویتشان عیناً از همین دو رابطه آمده به Unknown برمی‌گردند؛
            // ردیفی که حساب نقدی دارد یا طرف‌حسابش با این دو ستون هم‌خوان نیست (مثل کمیسیون
            // صراف) دست‌نخورده می‌ماند.
            migrationBuilder.Sql(@"
                UPDATE ""ExpenseTransactions""
                SET ""SettlementMode"" = 0,
                    ""CounterpartyType"" = NULL,
                    ""CounterpartyId"" = NULL
                WHERE ""SettlementMode"" = 1
                  AND ""CashAccountId"" IS NULL
                  AND (
                        (""CounterpartyType"" = 3 AND ""CounterpartyId"" = ""ServiceProviderId"")
                     OR (""CounterpartyType"" = 5 AND ""CounterpartyId"" = ""DriverId"")
                  );
            ");
        }
    }
}
