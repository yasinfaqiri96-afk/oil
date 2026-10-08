using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalPartyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some long-lived databases already have part of this operational-party
            // schema from an earlier manual/partial rollout. Keep the migration
            // idempotent so EF can finish recording it in __EFMigrationsHistory.
            migrationBuilder.Sql("""
                ALTER TABLE "TruckDispatches" ADD COLUMN IF NOT EXISTS "OperationalAssetId" integer;
                ALTER TABLE "TruckDispatches" ADD COLUMN IF NOT EXISTS "ServiceProviderId" integer;
                ALTER TABLE "InventoryTransportReceipts" ADD COLUMN IF NOT EXISTS "OperationalAssetId" integer;
                ALTER TABLE "InventoryTransportReceipts" ADD COLUMN IF NOT EXISTS "ServiceProviderId" integer;
                ALTER TABLE "InventoryTransportLegs" ADD COLUMN IF NOT EXISTS "OperationalAssetId" integer;
                ALTER TABLE "InventoryTransportLegs" ADD COLUMN IF NOT EXISTS "ServiceProviderId" integer;
                ALTER TABLE "AssetRentTransactions" ADD COLUMN IF NOT EXISTS "InventoryTransportReceiptId" integer;
                ALTER TABLE "AssetRentTransactions" ADD COLUMN IF NOT EXISTS "TransportLegId" integer;
                ALTER TABLE "AssetRentTransactions" ADD COLUMN IF NOT EXISTS "TruckDispatchId" integer;

                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_OperationalAssetId"
                    ON "TruckDispatches" ("OperationalAssetId");
                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_ServiceProviderId"
                    ON "TruckDispatches" ("ServiceProviderId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryTransportReceipts_OperationalAssetId"
                    ON "InventoryTransportReceipts" ("OperationalAssetId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryTransportReceipts_ServiceProviderId"
                    ON "InventoryTransportReceipts" ("ServiceProviderId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryTransportLegs_OperationalAssetId"
                    ON "InventoryTransportLegs" ("OperationalAssetId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryTransportLegs_ServiceProviderId"
                    ON "InventoryTransportLegs" ("ServiceProviderId");
                CREATE INDEX IF NOT EXISTS "IX_AssetRentTransactions_InventoryTransportReceiptId"
                    ON "AssetRentTransactions" ("InventoryTransportReceiptId");
                CREATE INDEX IF NOT EXISTS "IX_AssetRentTransactions_TransportLegId"
                    ON "AssetRentTransactions" ("TransportLegId");
                CREATE INDEX IF NOT EXISTS "IX_AssetRentTransactions_TruckDispatchId"
                    ON "AssetRentTransactions" ("TruckDispatchId");

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'AssetRentTransactions'
                          AND c.conname = 'FK_AssetRentTransactions_InventoryTransportLegs_TransportLegId'
                    ) THEN
                        ALTER TABLE "AssetRentTransactions"
                            ADD CONSTRAINT "FK_AssetRentTransactions_InventoryTransportLegs_TransportLegId"
                            FOREIGN KEY ("TransportLegId") REFERENCES "InventoryTransportLegs" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'AssetRentTransactions'
                          AND c.conname = 'FK_AssetRentTransactions_InventoryTransportReceipts_InventoryT~'
                    ) THEN
                        ALTER TABLE "AssetRentTransactions"
                            ADD CONSTRAINT "FK_AssetRentTransactions_InventoryTransportReceipts_InventoryT~"
                            FOREIGN KEY ("InventoryTransportReceiptId") REFERENCES "InventoryTransportReceipts" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'AssetRentTransactions'
                          AND c.conname = 'FK_AssetRentTransactions_TruckDispatches_TruckDispatchId'
                    ) THEN
                        ALTER TABLE "AssetRentTransactions"
                            ADD CONSTRAINT "FK_AssetRentTransactions_TruckDispatches_TruckDispatchId"
                            FOREIGN KEY ("TruckDispatchId") REFERENCES "TruckDispatches" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'InventoryTransportLegs'
                          AND c.conname = 'FK_InventoryTransportLegs_OperationalAssets_OperationalAssetId'
                    ) THEN
                        ALTER TABLE "InventoryTransportLegs"
                            ADD CONSTRAINT "FK_InventoryTransportLegs_OperationalAssets_OperationalAssetId"
                            FOREIGN KEY ("OperationalAssetId") REFERENCES "OperationalAssets" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'InventoryTransportLegs'
                          AND c.conname = 'FK_InventoryTransportLegs_ServiceProviders_ServiceProviderId'
                    ) THEN
                        ALTER TABLE "InventoryTransportLegs"
                            ADD CONSTRAINT "FK_InventoryTransportLegs_ServiceProviders_ServiceProviderId"
                            FOREIGN KEY ("ServiceProviderId") REFERENCES "ServiceProviders" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'InventoryTransportReceipts'
                          AND c.conname = 'FK_InventoryTransportReceipts_OperationalAssets_OperationalAss~'
                    ) THEN
                        ALTER TABLE "InventoryTransportReceipts"
                            ADD CONSTRAINT "FK_InventoryTransportReceipts_OperationalAssets_OperationalAss~"
                            FOREIGN KEY ("OperationalAssetId") REFERENCES "OperationalAssets" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'InventoryTransportReceipts'
                          AND c.conname = 'FK_InventoryTransportReceipts_ServiceProviders_ServiceProvider~'
                    ) THEN
                        ALTER TABLE "InventoryTransportReceipts"
                            ADD CONSTRAINT "FK_InventoryTransportReceipts_ServiceProviders_ServiceProvider~"
                            FOREIGN KEY ("ServiceProviderId") REFERENCES "ServiceProviders" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'TruckDispatches'
                          AND c.conname = 'FK_TruckDispatches_OperationalAssets_OperationalAssetId'
                    ) THEN
                        ALTER TABLE "TruckDispatches"
                            ADD CONSTRAINT "FK_TruckDispatches_OperationalAssets_OperationalAssetId"
                            FOREIGN KEY ("OperationalAssetId") REFERENCES "OperationalAssets" ("Id") ON DELETE RESTRICT;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'public'
                          AND t.relname = 'TruckDispatches'
                          AND c.conname = 'FK_TruckDispatches_ServiceProviders_ServiceProviderId'
                    ) THEN
                        ALTER TABLE "TruckDispatches"
                            ADD CONSTRAINT "FK_TruckDispatches_ServiceProviders_ServiceProviderId"
                            FOREIGN KEY ("ServiceProviderId") REFERENCES "ServiceProviders" ("Id") ON DELETE RESTRICT;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AssetRentTransactions" DROP CONSTRAINT IF EXISTS "FK_AssetRentTransactions_InventoryTransportLegs_TransportLegId";
                ALTER TABLE "AssetRentTransactions" DROP CONSTRAINT IF EXISTS "FK_AssetRentTransactions_InventoryTransportReceipts_InventoryT~";
                ALTER TABLE "AssetRentTransactions" DROP CONSTRAINT IF EXISTS "FK_AssetRentTransactions_TruckDispatches_TruckDispatchId";
                ALTER TABLE "InventoryTransportLegs" DROP CONSTRAINT IF EXISTS "FK_InventoryTransportLegs_OperationalAssets_OperationalAssetId";
                ALTER TABLE "InventoryTransportLegs" DROP CONSTRAINT IF EXISTS "FK_InventoryTransportLegs_ServiceProviders_ServiceProviderId";
                ALTER TABLE "InventoryTransportReceipts" DROP CONSTRAINT IF EXISTS "FK_InventoryTransportReceipts_OperationalAssets_OperationalAss~";
                ALTER TABLE "InventoryTransportReceipts" DROP CONSTRAINT IF EXISTS "FK_InventoryTransportReceipts_ServiceProviders_ServiceProvider~";
                ALTER TABLE "TruckDispatches" DROP CONSTRAINT IF EXISTS "FK_TruckDispatches_OperationalAssets_OperationalAssetId";
                ALTER TABLE "TruckDispatches" DROP CONSTRAINT IF EXISTS "FK_TruckDispatches_ServiceProviders_ServiceProviderId";

                DROP INDEX IF EXISTS "IX_TruckDispatches_OperationalAssetId";
                DROP INDEX IF EXISTS "IX_TruckDispatches_ServiceProviderId";
                DROP INDEX IF EXISTS "IX_InventoryTransportReceipts_OperationalAssetId";
                DROP INDEX IF EXISTS "IX_InventoryTransportReceipts_ServiceProviderId";
                DROP INDEX IF EXISTS "IX_InventoryTransportLegs_OperationalAssetId";
                DROP INDEX IF EXISTS "IX_InventoryTransportLegs_ServiceProviderId";
                DROP INDEX IF EXISTS "IX_AssetRentTransactions_InventoryTransportReceiptId";
                DROP INDEX IF EXISTS "IX_AssetRentTransactions_TransportLegId";
                DROP INDEX IF EXISTS "IX_AssetRentTransactions_TruckDispatchId";

                ALTER TABLE "TruckDispatches" DROP COLUMN IF EXISTS "OperationalAssetId";
                ALTER TABLE "TruckDispatches" DROP COLUMN IF EXISTS "ServiceProviderId";
                ALTER TABLE "InventoryTransportReceipts" DROP COLUMN IF EXISTS "OperationalAssetId";
                ALTER TABLE "InventoryTransportReceipts" DROP COLUMN IF EXISTS "ServiceProviderId";
                ALTER TABLE "InventoryTransportLegs" DROP COLUMN IF EXISTS "OperationalAssetId";
                ALTER TABLE "InventoryTransportLegs" DROP COLUMN IF EXISTS "ServiceProviderId";
                ALTER TABLE "AssetRentTransactions" DROP COLUMN IF EXISTS "InventoryTransportReceiptId";
                ALTER TABLE "AssetRentTransactions" DROP COLUMN IF EXISTS "TransportLegId";
                ALTER TABLE "AssetRentTransactions" DROP COLUMN IF EXISTS "TruckDispatchId";
                """);
        }
    }
}
