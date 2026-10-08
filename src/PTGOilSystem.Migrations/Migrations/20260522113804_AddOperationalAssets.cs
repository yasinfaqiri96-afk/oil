using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OperationalAssetId",
                table: "ExpenseTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OperationalAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssetType = table.Column<int>(type: "integer", nullable: false),
                    LinkedTruckId = table.Column<int>(type: "integer", nullable: true),
                    LinkedStorageTankId = table.Column<int>(type: "integer", nullable: true),
                    CapacityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    TerminalId = table.Column<int>(type: "integer", nullable: true),
                    OwnershipMode = table.Column<int>(type: "integer", nullable: false),
                    MonthlyDepreciationUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    DefaultInternalRateUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DefaultExternalRateUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationalAssets_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalAssets_StorageTanks_LinkedStorageTankId",
                        column: x => x.LinkedStorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalAssets_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalAssets_Trucks_LinkedTruckId",
                        column: x => x.LinkedTruckId,
                        principalTable: "Trucks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetOwnershipShares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>(type: "integer", nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SharePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetOwnershipShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetOwnershipShares_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOwnershipShares_OperationalAssets_OperationalAssetId",
                        column: x => x.OperationalAssetId,
                        principalTable: "OperationalAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOwnershipShares_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetRentTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>(type: "integer", nullable: false),
                    RentDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsageType = table.Column<int>(type: "integer", nullable: false),
                    ChargedToType = table.Column<int>(type: "integer", nullable: false),
                    ChargedToContractId = table.Column<int>(type: "integer", nullable: true),
                    ChargedToCustomerId = table.Column<int>(type: "integer", nullable: true),
                    ChargedToCompanyId = table.Column<int>(type: "integer", nullable: true),
                    ChargedToPartnerId = table.Column<int>(type: "integer", nullable: true),
                    ChargedToServiceProviderId = table.Column<int>(type: "integer", nullable: true),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DistanceKm = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Days = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Rate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "USD"),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AmountOriginal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceDocument = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsPostedToLedger = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<int>(type: "integer", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetRentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_Companies_ChargedToCompanyId",
                        column: x => x.ChargedToCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_Contracts_ChargedToContractId",
                        column: x => x.ChargedToContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_Customers_ChargedToCustomerId",
                        column: x => x.ChargedToCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_LedgerEntries_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_OperationalAssets_OperationalAssetId",
                        column: x => x.OperationalAssetId,
                        principalTable: "OperationalAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_Partners_ChargedToPartnerId",
                        column: x => x.ChargedToPartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentTransactions_ServiceProviders_ChargedToServiceProv~",
                        column: x => x.ChargedToServiceProviderId,
                        principalTable: "ServiceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetRentShares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetRentTransactionId = table.Column<int>(type: "integer", nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SharePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    ShareAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetRentShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetRentShares_AssetRentTransactions_AssetRentTransactionId",
                        column: x => x.AssetRentTransactionId,
                        principalTable: "AssetRentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentShares_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetRentShares_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseTransactions_OperationalAssetId",
                table: "ExpenseTransactions",
                column: "OperationalAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOwnershipShares_CompanyId",
                table: "AssetOwnershipShares",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOwnershipShares_OperationalAssetId_EffectiveFrom_Effec~",
                table: "AssetOwnershipShares",
                columns: new[] { "OperationalAssetId", "EffectiveFrom", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetOwnershipShares_PartnerId",
                table: "AssetOwnershipShares",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentShares_AssetRentTransactionId",
                table: "AssetRentShares",
                column: "AssetRentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentShares_CompanyId",
                table: "AssetRentShares",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentShares_PartnerId",
                table: "AssetRentShares",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ChargedToCompanyId",
                table: "AssetRentTransactions",
                column: "ChargedToCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ChargedToContractId",
                table: "AssetRentTransactions",
                column: "ChargedToContractId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ChargedToCustomerId",
                table: "AssetRentTransactions",
                column: "ChargedToCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ChargedToPartnerId",
                table: "AssetRentTransactions",
                column: "ChargedToPartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ChargedToServiceProviderId",
                table: "AssetRentTransactions",
                column: "ChargedToServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_LedgerEntryId",
                table: "AssetRentTransactions",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_OperationalAssetId_RentDate",
                table: "AssetRentTransactions",
                columns: new[] { "OperationalAssetId", "RentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetRentTransactions_ReferenceDocument",
                table: "AssetRentTransactions",
                column: "ReferenceDocument");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_AssetCode",
                table: "OperationalAssets",
                column: "AssetCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_AssetType",
                table: "OperationalAssets",
                column: "AssetType");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_IsActive_Name",
                table: "OperationalAssets",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_LinkedStorageTankId",
                table: "OperationalAssets",
                column: "LinkedStorageTankId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_LinkedTruckId",
                table: "OperationalAssets",
                column: "LinkedTruckId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_LocationId",
                table: "OperationalAssets",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAssets_TerminalId",
                table: "OperationalAssets",
                column: "TerminalId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExpenseTransactions_OperationalAssets_OperationalAssetId",
                table: "ExpenseTransactions",
                column: "OperationalAssetId",
                principalTable: "OperationalAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExpenseTransactions_OperationalAssets_OperationalAssetId",
                table: "ExpenseTransactions");

            migrationBuilder.DropTable(
                name: "AssetOwnershipShares");

            migrationBuilder.DropTable(
                name: "AssetRentShares");

            migrationBuilder.DropTable(
                name: "AssetRentTransactions");

            migrationBuilder.DropTable(
                name: "OperationalAssets");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseTransactions_OperationalAssetId",
                table: "ExpenseTransactions");

            migrationBuilder.DropColumn(
                name: "OperationalAssetId",
                table: "ExpenseTransactions");
        }
    }
}
