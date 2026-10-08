using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260519110000_RepairAuditLogsSchema")]
    public partial class RepairAuditLogsSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "ActionName" character varying(100);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "ActorUsername" character varying(150);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "Category" character varying(30);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "ControllerName" character varying(100);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "CorrelationId" character varying(80);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "Description" character varying(500);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "DurationMs" bigint;
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "HttpMethod" character varying(10);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "IpAddress" character varying(80);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "IsSuccess" boolean;
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "MetadataJson" character varying(4000);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "Module" character varying(100);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "RequestPath" character varying(260);
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "StatusCode" integer;
                ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "UserAgent" character varying(1000);

                UPDATE "AuditLogs"
                SET "Category" = 'Entity'
                WHERE "Category" IS NULL OR btrim("Category") = '';

                UPDATE "AuditLogs"
                SET "IsSuccess" = true
                WHERE "IsSuccess" IS NULL;

                ALTER TABLE "AuditLogs" ALTER COLUMN "Category" SET DEFAULT 'Entity';
                ALTER TABLE "AuditLogs" ALTER COLUMN "Category" SET NOT NULL;
                ALTER TABLE "AuditLogs" ALTER COLUMN "IsSuccess" SET DEFAULT true;
                ALTER TABLE "AuditLogs" ALTER COLUMN "IsSuccess" SET NOT NULL;

                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_ActorUserId" ON "AuditLogs" ("ActorUserId");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Category_ActionAtUtc" ON "AuditLogs" ("Category", "ActionAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_ControllerName" ON "AuditLogs" ("ControllerName");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_CorrelationId" ON "AuditLogs" ("CorrelationId");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_RequestPath" ON "AuditLogs" ("RequestPath");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left non-destructive: this migration repairs a drifted
            // production/local audit schema and should not remove logged history.
        }
    }
}
