using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAssetAccountingDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartnerId",
                table: "LedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperationalAssetId",
                table: "JournalEntryLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccumulatedDepreciationAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssetOperatingExpenseAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssetRentalRevenueAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DepreciationExpenseAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FixedAssetAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InternalAssetRecoveryAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_PartnerId",
                table: "LedgerEntries",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_OperationalAssetId",
                table: "JournalEntryLines",
                column: "OperationalAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AccumulatedDepreciationAccountId",
                table: "AccountingSettings",
                column: "AccumulatedDepreciationAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AssetOperatingExpenseAccountId",
                table: "AccountingSettings",
                column: "AssetOperatingExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_AssetRentalRevenueAccountId",
                table: "AccountingSettings",
                column: "AssetRentalRevenueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_DepreciationExpenseAccountId",
                table: "AccountingSettings",
                column: "DepreciationExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_FixedAssetAccountId",
                table: "AccountingSettings",
                column: "FixedAssetAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_InternalAssetRecoveryAccountId",
                table: "AccountingSettings",
                column: "InternalAssetRecoveryAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_AccumulatedDepreciationAccountId",
                table: "AccountingSettings",
                column: "AccumulatedDepreciationAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_AssetOperatingExpenseAccountId",
                table: "AccountingSettings",
                column: "AssetOperatingExpenseAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_AssetRentalRevenueAccountId",
                table: "AccountingSettings",
                column: "AssetRentalRevenueAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_DepreciationExpenseAccountId",
                table: "AccountingSettings",
                column: "DepreciationExpenseAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_FixedAssetAccountId",
                table: "AccountingSettings",
                column: "FixedAssetAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_InternalAssetRecoveryAccountId",
                table: "AccountingSettings",
                column: "InternalAssetRecoveryAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntryLines_OperationalAssets_OperationalAssetId",
                table: "JournalEntryLines",
                column: "OperationalAssetId",
                principalTable: "OperationalAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_Partners_PartnerId",
                table: "LedgerEntries",
                column: "PartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_AccumulatedDepreciationAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_AssetOperatingExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_AssetRentalRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_DepreciationExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_FixedAssetAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_InternalAssetRecoveryAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntryLines_OperationalAssets_OperationalAssetId",
                table: "JournalEntryLines");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_Partners_PartnerId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_PartnerId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntryLines_OperationalAssetId",
                table: "JournalEntryLines");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_AccumulatedDepreciationAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_AssetOperatingExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_AssetRentalRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_DepreciationExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_FixedAssetAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_InternalAssetRecoveryAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "PartnerId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "OperationalAssetId",
                table: "JournalEntryLines");

            migrationBuilder.DropColumn(
                name: "AccumulatedDepreciationAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "AssetOperatingExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "AssetRentalRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "DepreciationExpenseAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "FixedAssetAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "InternalAssetRecoveryAccountId",
                table: "AccountingSettings");
        }
    }
}
