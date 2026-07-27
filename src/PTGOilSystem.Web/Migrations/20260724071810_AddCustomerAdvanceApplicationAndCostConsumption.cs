using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAdvanceApplicationAndCostConsumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // گاردِ اصلاحی برای مهاجرتِ قبلی (AddCustomerPaymentAllocation): آن مهاجرت ستونِ
            // PaymentTransactions."PreSaleOrderId" را حذف می‌کند و فقط لینک‌هایی را که نرخِ معتبر
            // دارند به CustomerPaymentAllocations منتقل می‌کند. اگر — در هر پایگاه‌داده‌ای که این
            // مهاجرت روی آن اجرا می‌شود — آن ستون هنوز وجود داشته باشد و لینکی باقی مانده باشد که
            // منتقل نشده (مثلاً نرخِ نامعتبر)، به‌جای حذفِ خاموشِ لینکِ مالی، مهاجرت با پیامِ واضح
            // شکست می‌خورد تا اپراتور آن را دستی و با نرخِ درست حل کند. در مسیرِ عادی (ستون قبلاً
            // حذف شده) این بلاک بی‌اثر است.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    col_exists boolean;
                    orphan_count integer;
                BEGIN
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'PaymentTransactions'
                          AND column_name = 'PreSaleOrderId'
                    ) INTO col_exists;

                    IF col_exists THEN
                        SELECT COUNT(*) INTO orphan_count
                        FROM "PaymentTransactions" p
                        WHERE p."PreSaleOrderId" IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM "CustomerPaymentAllocations" a
                              WHERE a."PaymentTransactionId" = p."Id"
                                AND a."PreSaleOrderId" = p."PreSaleOrderId"
                          );

                        IF orphan_count > 0 THEN
                            RAISE EXCEPTION 'Found % PaymentTransaction(s) with a PreSaleOrderId link that was not migrated to CustomerPaymentAllocations (likely an invalid FX rate). Re-record these links manually with a valid rate before the column is dropped; refusing to drop financial links silently.', orphan_count;
                        END IF;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "CustomerPaymentAllocationApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerPaymentAllocationId = table.Column<int>(type: "integer", nullable: false),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedPaymentAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PaymentCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppliedAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReversalOfApplicationId = table.Column<int>(type: "integer", nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentAllocationApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocationApplications_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocationApplications_CustomerPaymentAlloca~",
                        column: x => x.CustomerPaymentAllocationId,
                        principalTable: "CustomerPaymentAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocationApplications_JournalEntries_Journa~",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocationApplications_SalesTransactions_Sal~",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesCostConsumptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesTransactionId = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesCostConsumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesCostConsumptions_SalesTransactions_SalesTransactionId",
                        column: x => x.SalesTransactionId,
                        principalTable: "SalesTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocationApplications_CompanyId",
                table: "CustomerPaymentAllocationApplications",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocationApplications_CustomerPaymentAlloca~",
                table: "CustomerPaymentAllocationApplications",
                columns: new[] { "CustomerPaymentAllocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocationApplications_JournalEntryId",
                table: "CustomerPaymentAllocationApplications",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocationApplications_SalesTransactionId_St~",
                table: "CustomerPaymentAllocationApplications",
                columns: new[] { "SalesTransactionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesCostConsumptions_SalesTransactionId_Status",
                table: "SalesCostConsumptions",
                columns: new[] { "SalesTransactionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerPaymentAllocationApplications");

            migrationBuilder.DropTable(
                name: "SalesCostConsumptions");
        }
    }
}
