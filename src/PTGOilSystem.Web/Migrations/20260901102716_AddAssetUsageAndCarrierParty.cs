using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetUsageAndCarrierParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyId",
                table: "TruckDispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyType",
                table: "TruckDispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyId",
                table: "LoadingExpenseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyType",
                table: "LoadingExpenseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyId",
                table: "InventoryTransportReceipts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyType",
                table: "InventoryTransportReceipts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyId",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarrierPartyType",
                table: "InventoryTransportLegs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>(type: "integer", nullable: false),
                    DocumentType = table.Column<int>(type: "integer", nullable: false),
                    DocumentId = table.Column<int>(type: "integer", nullable: false),
                    UsageDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DistanceKm = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Days = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    FromLocationId = table.Column<int>(type: "integer", nullable: true),
                    ToLocationId = table.Column<int>(type: "integer", nullable: true),
                    IsReversed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetUsages_Locations_FromLocationId",
                        column: x => x.FromLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetUsages_Locations_ToLocationId",
                        column: x => x.ToLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetUsages_OperationalAssets_OperationalAssetId",
                        column: x => x.OperationalAssetId,
                        principalTable: "OperationalAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetCharges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetUsageId = table.Column<int>(type: "integer", nullable: false),
                    ChargeKind = table.Column<int>(type: "integer", nullable: false),
                    RateBasis = table.Column<int>(type: "integer", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    QuantityBasis = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "USD"),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AmountOriginal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CounterpartyPartyType = table.Column<int>(type: "integer", nullable: true),
                    CounterpartyPartyId = table.Column<int>(type: "integer", nullable: true),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    PostingStatus = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SkipReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    LedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    LegacyAssetRentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetCharges_AssetRentTransactions_LegacyAssetRentTransacti~",
                        column: x => x.LegacyAssetRentTransactionId,
                        principalTable: "AssetRentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCharges_AssetUsages_AssetUsageId",
                        column: x => x.AssetUsageId,
                        principalTable: "AssetUsages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCharges_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCharges_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCharges_LedgerEntries_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TruckDispatches_CarrierPartyType_CarrierPartyId",
                table: "TruckDispatches",
                columns: new[] { "CarrierPartyType", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_LoadingExpenseLines_CarrierPartyType_CarrierPartyId",
                table: "LoadingExpenseLines",
                columns: new[] { "CarrierPartyType", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportReceipts_CarrierPartyType_CarrierPartyId",
                table: "InventoryTransportReceipts",
                columns: new[] { "CarrierPartyType", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransportLegs_CarrierPartyType_CarrierPartyId",
                table: "InventoryTransportLegs",
                columns: new[] { "CarrierPartyType", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_AssetUsageId_ChargeKind",
                table: "AssetCharges",
                columns: new[] { "AssetUsageId", "ChargeKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_ContractId",
                table: "AssetCharges",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_CounterpartyPartyType_CounterpartyPartyId",
                table: "AssetCharges",
                columns: new[] { "CounterpartyPartyType", "CounterpartyPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_JournalEntryId",
                table: "AssetCharges",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_LedgerEntryId",
                table: "AssetCharges",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCharges_LegacyAssetRentTransactionId",
                table: "AssetCharges",
                column: "LegacyAssetRentTransactionId",
                unique: true,
                filter: "\"LegacyAssetRentTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssetUsages_FromLocationId",
                table: "AssetUsages",
                column: "FromLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetUsages_OperationalAssetId_DocumentType_DocumentId",
                table: "AssetUsages",
                columns: new[] { "OperationalAssetId", "DocumentType", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetUsages_ToLocationId",
                table: "AssetUsages",
                column: "ToLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetUsages_UsageDate",
                table: "AssetUsages",
                column: "UsageDate");

            // Deterministic carrier backfill only. Ambiguous/shared ownership remains unresolved.
            migrationBuilder.Sql("""
                UPDATE "InventoryTransportLegs"
                SET "CarrierPartyType" = 3, "CarrierPartyId" = "ServiceProviderId"
                WHERE "CarrierPartyId" IS NULL AND "ServiceProviderId" IS NOT NULL;

                UPDATE "InventoryTransportLegs" l
                SET "CarrierPartyType" = CASE s."OwnerType" WHEN 1 THEN 8 WHEN 2 THEN 7 END,
                    "CarrierPartyId" = COALESCE(s."CompanyId", s."PartnerId")
                FROM "AssetOwnershipShares" s
                WHERE l."CarrierPartyId" IS NULL
                  AND l."OperationalAssetId" = s."OperationalAssetId"
                  AND s."EffectiveFrom" <= l."LoadedDate"
                  AND (s."EffectiveTo" IS NULL OR s."EffectiveTo" >= l."LoadedDate")
                  AND s."SharePercent" = 100
                  AND ((s."OwnerType" = 1 AND s."CompanyId" IS NOT NULL) OR (s."OwnerType" = 2 AND s."PartnerId" IS NOT NULL))
                  AND (SELECT COUNT(*) FROM "AssetOwnershipShares" x
                       WHERE x."OperationalAssetId" = l."OperationalAssetId"
                         AND x."EffectiveFrom" <= l."LoadedDate"
                         AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= l."LoadedDate")) = 1;

                UPDATE "InventoryTransportLegs"
                SET "CarrierPartyType" = 5, "CarrierPartyId" = "DriverId"
                WHERE "CarrierPartyId" IS NULL AND "DriverId" IS NOT NULL;

                UPDATE "InventoryTransportReceipts"
                SET "CarrierPartyType" = 3, "CarrierPartyId" = "ServiceProviderId"
                WHERE "CarrierPartyId" IS NULL AND "ServiceProviderId" IS NOT NULL;

                UPDATE "InventoryTransportReceipts" r
                SET "CarrierPartyType" = l."CarrierPartyType", "CarrierPartyId" = l."CarrierPartyId"
                FROM "InventoryTransportLegs" l
                WHERE r."CarrierPartyId" IS NULL
                  AND r."InventoryTransportLegId" = l."Id"
                  AND l."CarrierPartyId" IS NOT NULL;

                UPDATE "TruckDispatches"
                SET "CarrierPartyType" = 3, "CarrierPartyId" = "ServiceProviderId"
                WHERE "CarrierPartyId" IS NULL AND "ServiceProviderId" IS NOT NULL;

                UPDATE "TruckDispatches" d
                SET "CarrierPartyType" = CASE s."OwnerType" WHEN 1 THEN 8 WHEN 2 THEN 7 END,
                    "CarrierPartyId" = COALESCE(s."CompanyId", s."PartnerId")
                FROM "AssetOwnershipShares" s
                WHERE d."CarrierPartyId" IS NULL
                  AND d."OperationalAssetId" = s."OperationalAssetId"
                  AND s."EffectiveFrom" <= d."DispatchDate"
                  AND (s."EffectiveTo" IS NULL OR s."EffectiveTo" >= d."DispatchDate")
                  AND s."SharePercent" = 100
                  AND ((s."OwnerType" = 1 AND s."CompanyId" IS NOT NULL) OR (s."OwnerType" = 2 AND s."PartnerId" IS NOT NULL))
                  AND (SELECT COUNT(*) FROM "AssetOwnershipShares" x
                       WHERE x."OperationalAssetId" = d."OperationalAssetId"
                         AND x."EffectiveFrom" <= d."DispatchDate"
                         AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= d."DispatchDate")) = 1;

                UPDATE "TruckDispatches"
                SET "CarrierPartyType" = 5, "CarrierPartyId" = "DriverId"
                WHERE "CarrierPartyId" IS NULL AND "DriverId" IS NOT NULL;

                UPDATE "LoadingExpenseLines"
                SET "CarrierPartyType" = 3, "CarrierPartyId" = "ServiceProviderId"
                WHERE "CarrierPartyId" IS NULL AND "ServiceProviderId" IS NOT NULL;

                UPDATE "LoadingExpenseLines" e
                SET "CarrierPartyType" = CASE s."OwnerType" WHEN 1 THEN 8 WHEN 2 THEN 7 END,
                    "CarrierPartyId" = COALESCE(s."CompanyId", s."PartnerId")
                FROM "AssetOwnershipShares" s, "LoadingRegisters" l
                WHERE e."CarrierPartyId" IS NULL
                  AND e."LoadingRegisterId" = l."Id"
                  AND e."OperationalAssetId" = s."OperationalAssetId"
                  AND s."EffectiveFrom" <= l."LoadingDate"
                  AND (s."EffectiveTo" IS NULL OR s."EffectiveTo" >= l."LoadingDate")
                  AND s."SharePercent" = 100
                  AND ((s."OwnerType" = 1 AND s."CompanyId" IS NOT NULL) OR (s."OwnerType" = 2 AND s."PartnerId" IS NOT NULL))
                  AND (SELECT COUNT(*) FROM "AssetOwnershipShares" x
                       WHERE x."OperationalAssetId" = e."OperationalAssetId"
                         AND x."EffectiveFrom" <= l."LoadingDate"
                         AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= l."LoadingDate")) = 1;
                """);

            // Operational history contains no money. Cancellation of a legacy rent does not reverse usage.
            migrationBuilder.Sql("""
                INSERT INTO "AssetUsages"
                    ("OperationalAssetId", "DocumentType", "DocumentId", "UsageDate", "QuantityMt", "DistanceKm", "Days", "FromLocationId", "ToLocationId", "IsReversed", "CreatedAtUtc")
                SELECT r."OperationalAssetId",
                       CASE WHEN r."LoadingRegisterId" IS NOT NULL THEN 1
                            WHEN r."TransportLegId" IS NOT NULL THEN 2
                            WHEN r."InventoryTransportReceiptId" IS NOT NULL THEN 3
                            WHEN r."TruckDispatchId" IS NOT NULL THEN 4 ELSE 5 END,
                       COALESCE(r."LoadingRegisterId", r."TransportLegId", r."InventoryTransportReceiptId", r."TruckDispatchId", r."Id"),
                       r."RentDate", r."QuantityMt", r."DistanceKm", r."Days", NULL, NULL, false, NOW()
                FROM "AssetRentTransactions" r
                ON CONFLICT ("OperationalAssetId", "DocumentType", "DocumentId") DO NOTHING;

                INSERT INTO "AssetUsages"
                    ("OperationalAssetId", "DocumentType", "DocumentId", "UsageDate", "QuantityMt", "IsReversed", "CreatedAtUtc")
                SELECT l."OperationalAssetId", 2, l."Id", l."LoadedDate", l."QuantityMt", l."Status" = 4, NOW()
                FROM "InventoryTransportLegs" l
                WHERE l."OperationalAssetId" IS NOT NULL
                ON CONFLICT ("OperationalAssetId", "DocumentType", "DocumentId") DO NOTHING;

                INSERT INTO "AssetUsages"
                    ("OperationalAssetId", "DocumentType", "DocumentId", "UsageDate", "QuantityMt", "ToLocationId", "IsReversed", "CreatedAtUtc")
                SELECT COALESCE(r."OperationalAssetId", l."OperationalAssetId"), 3, r."Id", r."ReceiptDate", r."ReceivedQuantityMt",
                       l."DestinationLocationId", r."IsCancelled", NOW()
                FROM "InventoryTransportReceipts" r
                JOIN "InventoryTransportLegs" l ON l."Id" = r."InventoryTransportLegId"
                WHERE COALESCE(r."OperationalAssetId", l."OperationalAssetId") IS NOT NULL
                ON CONFLICT ("OperationalAssetId", "DocumentType", "DocumentId") DO NOTHING;

                INSERT INTO "AssetUsages"
                    ("OperationalAssetId", "DocumentType", "DocumentId", "UsageDate", "QuantityMt", "ToLocationId", "IsReversed", "CreatedAtUtc")
                SELECT d."OperationalAssetId", 4, d."Id", d."DispatchDate", d."LoadedQuantityMt", d."DestinationLocationId", d."Status" = 4, NOW()
                FROM "TruckDispatches" d
                WHERE d."OperationalAssetId" IS NOT NULL
                ON CONFLICT ("OperationalAssetId", "DocumentType", "DocumentId") DO NOTHING;
                """);

            // Only unambiguous legacy rows are mapped. Duplicate historical rent rows remain readable in the legacy table.
            migrationBuilder.Sql("""
                WITH candidates AS (
                    SELECT r.*,
                           CASE WHEN r."LoadingRegisterId" IS NOT NULL THEN 1
                                WHEN r."TransportLegId" IS NOT NULL THEN 2
                                WHEN r."InventoryTransportReceiptId" IS NOT NULL THEN 3
                                WHEN r."TruckDispatchId" IS NOT NULL THEN 4 ELSE 5 END AS document_type,
                           COALESCE(r."LoadingRegisterId", r."TransportLegId", r."InventoryTransportReceiptId", r."TruckDispatchId", r."Id") AS document_id,
                           CASE WHEN r."UsageType" = 1 THEN 1 ELSE 2 END AS charge_kind
                    FROM "AssetRentTransactions" r
                ), eligible AS (
                    SELECT c.*, COUNT(*) OVER (PARTITION BY c."OperationalAssetId", c.document_type, c.document_id, c.charge_kind) AS duplicate_count
                    FROM candidates c
                )
                INSERT INTO "AssetCharges"
                    ("AssetUsageId", "ChargeKind", "RateBasis", "Rate", "QuantityBasis", "Currency", "FxRateToUsd",
                     "AmountOriginal", "AmountUsd", "CounterpartyPartyType", "CounterpartyPartyId", "ContractId",
                     "PostingStatus", "SkipReason", "LedgerEntryId", "LegacyAssetRentTransactionId", "IsCancelled", "CreatedAtUtc")
                SELECT u."Id", e.charge_kind,
                       CASE WHEN e."Days" > 0 THEN 4 WHEN e."DistanceKm" > 0 THEN 3 WHEN e."QuantityMt" > 0 THEN 2 ELSE 1 END,
                       e."Rate", COALESCE(e."Days", e."DistanceKm", e."QuantityMt"), e."Currency", e."FxRateToUsd",
                       e."AmountOriginal", e."AmountUsd",
                       CASE WHEN e."ChargedToCustomerId" IS NOT NULL THEN 1
                            WHEN e."ChargedToServiceProviderId" IS NOT NULL THEN 3
                            WHEN e."ChargedToPartnerId" IS NOT NULL THEN 7
                            WHEN e."ChargedToCompanyId" IS NOT NULL THEN 8
                            WHEN c."ContractType" = 1 AND c."SupplierId" IS NOT NULL THEN 2
                            WHEN c."ContractType" = 2 AND c."CustomerId" IS NOT NULL THEN 1
                            WHEN c."CompanyId" IS NOT NULL THEN 8 END,
                       COALESCE(e."ChargedToCustomerId", e."ChargedToServiceProviderId", e."ChargedToPartnerId", e."ChargedToCompanyId",
                                CASE WHEN c."ContractType" = 1 THEN c."SupplierId" WHEN c."ContractType" = 2 THEN c."CustomerId" END, c."CompanyId"),
                       e."ChargedToContractId",
                       CASE WHEN e."IsCancelled" THEN 3 WHEN e."UsageType" NOT IN (1, 2) THEN 2 WHEN e."IsPostedToLedger" THEN 1 ELSE 0 END,
                       CASE WHEN e."UsageType" NOT IN (1, 2) THEN 'Legacy usage type requires manual classification.' ELSE e."CancelReason" END,
                       e."LedgerEntryId", e."Id", e."IsCancelled", NOW()
                FROM eligible e
                JOIN "AssetUsages" u ON u."OperationalAssetId" = e."OperationalAssetId"
                    AND u."DocumentType" = e.document_type AND u."DocumentId" = e.document_id
                LEFT JOIN "Contracts" c ON c."Id" = e."ChargedToContractId"
                WHERE e.duplicate_count = 1
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetCharges");

            migrationBuilder.DropTable(
                name: "AssetUsages");

            migrationBuilder.DropIndex(
                name: "IX_TruckDispatches_CarrierPartyType_CarrierPartyId",
                table: "TruckDispatches");

            migrationBuilder.DropIndex(
                name: "IX_LoadingExpenseLines_CarrierPartyType_CarrierPartyId",
                table: "LoadingExpenseLines");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportReceipts_CarrierPartyType_CarrierPartyId",
                table: "InventoryTransportReceipts");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransportLegs_CarrierPartyType_CarrierPartyId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "CarrierPartyId",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "CarrierPartyType",
                table: "TruckDispatches");

            migrationBuilder.DropColumn(
                name: "CarrierPartyId",
                table: "LoadingExpenseLines");

            migrationBuilder.DropColumn(
                name: "CarrierPartyType",
                table: "LoadingExpenseLines");

            migrationBuilder.DropColumn(
                name: "CarrierPartyId",
                table: "InventoryTransportReceipts");

            migrationBuilder.DropColumn(
                name: "CarrierPartyType",
                table: "InventoryTransportReceipts");

            migrationBuilder.DropColumn(
                name: "CarrierPartyId",
                table: "InventoryTransportLegs");

            migrationBuilder.DropColumn(
                name: "CarrierPartyType",
                table: "InventoryTransportLegs");
        }
    }
}
