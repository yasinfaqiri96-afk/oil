using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDriveCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProtectedRefreshToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    FolderId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConnectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTokenRefreshAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDriveCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggerKind = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StorageTarget = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    RetentionTier = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DriveUploadStatus = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DriveFileId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DriveResumeUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DriveUploadAttempts = table.Column<int>(type: "integer", nullable: false),
                    DriveUploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUsername = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ManifestJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AutoBackupEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Schedule = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    RunAtHourLocal = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    RunAtMinuteLocal = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    StorageTarget = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    KeepDaily = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    KeepWeekly = table.Column<int>(type: "integer", nullable: false, defaultValue: 4),
                    KeepMonthly = table.Column<int>(type: "integer", nullable: false, defaultValue: 6),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_FileName",
                table: "BackupJobs",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_StartedAtUtc",
                table: "BackupJobs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_Status",
                table: "BackupJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDriveCredentials");

            migrationBuilder.DropTable(
                name: "BackupJobs");

            migrationBuilder.DropTable(
                name: "BackupSettings");
        }
    }
}
