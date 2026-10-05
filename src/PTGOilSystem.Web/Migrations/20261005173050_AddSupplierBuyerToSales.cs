using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBuyerToSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_Customers_CustomerId",
                table: "SalesTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "SalesBatches",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "SalesBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "PreSaleOrders",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "PreSaleOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_SupplierId",
                table: "SalesTransactions",
                column: "SupplierId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesTransactions_ExactlyOneBuyer",
                table: "SalesTransactions",
                sql: "(\"CustomerId\" IS NOT NULL) <> (\"SupplierId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_SalesBatches_SupplierId",
                table: "SalesBatches",
                column: "SupplierId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesBatches_ExactlyOneBuyer",
                table: "SalesBatches",
                sql: "(\"CustomerId\" IS NOT NULL) <> (\"SupplierId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PreSaleOrders_SupplierId",
                table: "PreSaleOrders",
                column: "SupplierId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PreSaleOrders_ExactlyOneBuyer",
                table: "PreSaleOrders",
                sql: "(\"CustomerId\" IS NOT NULL) <> (\"SupplierId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_PreSaleOrders_Suppliers_SupplierId",
                table: "PreSaleOrders",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesBatches_Suppliers_SupplierId",
                table: "SalesBatches",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_Customers_CustomerId",
                table: "SalesTransactions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_Suppliers_SupplierId",
                table: "SalesTransactions",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "SalesTransactions" WHERE "SupplierId" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM "SalesBatches" WHERE "SupplierId" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM "PreSaleOrders" WHERE "SupplierId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot roll back AddSupplierBuyerToSales while supplier-buyer sales exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PreSaleOrders_Suppliers_SupplierId",
                table: "PreSaleOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesBatches_Suppliers_SupplierId",
                table: "SalesBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_Customers_CustomerId",
                table: "SalesTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesTransactions_Suppliers_SupplierId",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_SupplierId",
                table: "SalesTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesTransactions_ExactlyOneBuyer",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SalesBatches_SupplierId",
                table: "SalesBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesBatches_ExactlyOneBuyer",
                table: "SalesBatches");

            migrationBuilder.DropIndex(
                name: "IX_PreSaleOrders_SupplierId",
                table: "PreSaleOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PreSaleOrders_ExactlyOneBuyer",
                table: "PreSaleOrders");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "SalesBatches");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "PreSaleOrders");

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "SalesTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "SalesBatches",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "PreSaleOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesTransactions_Customers_CustomerId",
                table: "SalesTransactions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
