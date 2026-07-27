using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerPaymentAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: false),
                    PreSaleOrderId = table.Column<int>(type: "integer", nullable: false),
                    AllocationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllocatedPaymentAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PaymentCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PaymentFxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AllocatedAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReversalOfAllocationId = table.Column<int>(type: "integer", nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_PaymentTransactions_PaymentTrans~",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_PreSaleOrders_PreSaleOrderId",
                        column: x => x.PreSaleOrderId,
                        principalTable: "PreSaleOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_PaymentTransactionId_Status",
                table: "CustomerPaymentAllocations",
                columns: new[] { "PaymentTransactionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_PreSaleOrderId_Status",
                table: "CustomerPaymentAllocations",
                columns: new[] { "PreSaleOrderId", "Status" });

            // ستون PaymentTransactions."PreSaleOrderId" یک لینکِ کاملِ «همهٔ پرداخت به یک پیش‌فروش»
            // بود و جای تخصیص جزئی/چندگانه را نمی‌گرفت. پیش از حذف، هر لینک موجود به یک تخصیصِ
            // فعال به مبلغ کاملِ همان دریافت تبدیل می‌شود تا هیچ داده‌ای از دست نرود.
            // دریافت‌های بدون نرخ معتبر عمداً منتقل نمی‌شوند: معادل دلاری آن‌ها قابل اثبات نیست و
            // ساختن نرخ برایشان یعنی جعل داده؛ لینکشان باید دستی و با نرخ درست دوباره ثبت شود.
            migrationBuilder.Sql("""
                INSERT INTO "CustomerPaymentAllocations" (
                    "PaymentTransactionId", "PreSaleOrderId", "AllocationDate",
                    "AllocatedPaymentAmount", "PaymentCurrencyCode", "PaymentFxRateToUsd",
                    "AllocatedAmountUsd", "Status", "Notes", "CreatedAtUtc")
                SELECT
                    p."Id", p."PreSaleOrderId", p."PaymentDate",
                    p."Amount", p."Currency", p."AppliedFxRateToUsd",
                    p."AmountUsd", 1, 'Migrated from PaymentTransaction.PreSaleOrderId', now()
                FROM "PaymentTransactions" p
                WHERE p."PreSaleOrderId" IS NOT NULL
                  AND p."Amount" > 0
                  AND COALESCE(p."AppliedFxRateToUsd", 0) > 0;
                """);

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_PreSaleOrderId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "PreSaleOrderId",
                table: "PaymentTransactions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerPaymentAllocations");

            migrationBuilder.AddColumn<int>(
                name: "PreSaleOrderId",
                table: "PaymentTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_PreSaleOrderId",
                table: "PaymentTransactions",
                column: "PreSaleOrderId");
        }
    }
}
