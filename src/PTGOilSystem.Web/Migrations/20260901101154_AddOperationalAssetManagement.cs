using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAssetManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>("AcquisitionCostUsd", "OperationalAssets", "numeric(18,4)", nullable: true);
            migrationBuilder.AddColumn<DateTime>("AcquisitionDate", "OperationalAssets", "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>("DisposalDate", "OperationalAssets", "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>("InServiceDate", "OperationalAssets", "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "OperationalStatus",
                table: "OperationalAssets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AssetAssignments",
                columns: table => new
                {
                    Id = table.Column<int>("integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>("integer", nullable: false),
                    ResponsiblePartyType = table.Column<int>("integer", nullable: false),
                    ResponsiblePartyId = table.Column<int>("integer", nullable: false),
                    DriverId = table.Column<int>("integer", nullable: true),
                    BaseTerminalId = table.Column<int>("integer", nullable: true),
                    Role = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
                    FromDate = table.Column<DateTime>("timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    Notes = table.Column<string>("character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>("integer", nullable: true),
                    UpdatedByUserId = table.Column<int>("integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetAssignments", x => x.Id);
                    table.ForeignKey("FK_AssetAssignments_Drivers_DriverId", x => x.DriverId, "Drivers", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_AssetAssignments_OperationalAssets_OperationalAssetId", x => x.OperationalAssetId, "OperationalAssets", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_AssetAssignments_Terminals_BaseTerminalId", x => x.BaseTerminalId, "Terminals", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetDocuments",
                columns: table => new
                {
                    Id = table.Column<int>("integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>("integer", nullable: false),
                    DocumentType = table.Column<int>("integer", nullable: false),
                    DocumentNumber = table.Column<string>("character varying(200)", maxLength: 200, nullable: true),
                    IssueDate = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    ExpiryDate = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    OriginalFileName = table.Column<string>("character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>("character varying(260)", maxLength: 260, nullable: false),
                    FilePath = table.Column<string>("character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>("character varying(200)", maxLength: 200, nullable: true),
                    FileSizeBytes = table.Column<long>("bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: false),
                    UploadedByUserName = table.Column<string>("character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>("character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>("integer", nullable: true),
                    UpdatedByUserId = table.Column<int>("integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetDocuments", x => x.Id);
                    table.ForeignKey("FK_AssetDocuments_OperationalAssets_OperationalAssetId", x => x.OperationalAssetId, "OperationalAssets", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetMaintenanceJobs",
                columns: table => new
                {
                    Id = table.Column<int>("integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>("integer", nullable: false), JobType = table.Column<int>("integer", nullable: false), Status = table.Column<int>("integer", nullable: false),
                    Title = table.Column<string>("character varying(200)", maxLength: 200, nullable: false),
                    ScheduledDate = table.Column<DateTime>("timestamp with time zone", nullable: true), StartedDate = table.Column<DateTime>("timestamp with time zone", nullable: true), CompletedDate = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    DowntimeFrom = table.Column<DateTime>("timestamp with time zone", nullable: true), DowntimeTo = table.Column<DateTime>("timestamp with time zone", nullable: true),
                    ExpenseTransactionId = table.Column<int>("integer", nullable: true), Notes = table.Column<string>("character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: false), UpdatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: true), CreatedByUserId = table.Column<int>("integer", nullable: true), UpdatedByUserId = table.Column<int>("integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetMaintenanceJobs", x => x.Id);
                    table.ForeignKey("FK_AssetMaintenanceJobs_ExpenseTransactions_ExpenseTransactionId", x => x.ExpenseTransactionId, "ExpenseTransactions", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_AssetMaintenanceJobs_OperationalAssets_OperationalAssetId", x => x.OperationalAssetId, "OperationalAssets", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetMeterReadings",
                columns: table => new
                {
                    Id = table.Column<int>("integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationalAssetId = table.Column<int>("integer", nullable: false), MeterType = table.Column<int>("integer", nullable: false), ReadingDate = table.Column<DateTime>("timestamp with time zone", nullable: false),
                    ReadingValue = table.Column<decimal>("numeric(18,4)", nullable: false), Reference = table.Column<string>("character varying(200)", maxLength: 200, nullable: true), Notes = table.Column<string>("character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: false), UpdatedAtUtc = table.Column<DateTime>("timestamp with time zone", nullable: true), CreatedByUserId = table.Column<int>("integer", nullable: true), UpdatedByUserId = table.Column<int>("integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetMeterReadings", x => x.Id);
                    table.ForeignKey("FK_AssetMeterReadings_OperationalAssets_OperationalAssetId", x => x.OperationalAssetId, "OperationalAssets", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex("IX_AssetAssignments_BaseTerminalId", "AssetAssignments", "BaseTerminalId");
            migrationBuilder.CreateIndex("IX_AssetAssignments_DriverId", "AssetAssignments", "DriverId");
            migrationBuilder.CreateIndex("IX_AssetAssignments_OperationalAssetId_Role", "AssetAssignments", new[] { "OperationalAssetId", "Role" }, unique: true, filter: "\"ToDate\" IS NULL");
            migrationBuilder.CreateIndex("IX_AssetAssignments_ResponsiblePartyType_ResponsiblePartyId", "AssetAssignments", new[] { "ResponsiblePartyType", "ResponsiblePartyId" });
            migrationBuilder.CreateIndex("IX_AssetDocuments_OperationalAssetId_DocumentType_ExpiryDate", "AssetDocuments", new[] { "OperationalAssetId", "DocumentType", "ExpiryDate" });
            migrationBuilder.CreateIndex("IX_AssetMaintenanceJobs_ExpenseTransactionId", "AssetMaintenanceJobs", "ExpenseTransactionId");
            migrationBuilder.CreateIndex("IX_AssetMaintenanceJobs_OperationalAssetId_Status_ScheduledDate", "AssetMaintenanceJobs", new[] { "OperationalAssetId", "Status", "ScheduledDate" });
            migrationBuilder.CreateIndex("IX_AssetMeterReadings_OperationalAssetId_MeterType_ReadingDate", "AssetMeterReadings", new[] { "OperationalAssetId", "MeterType", "ReadingDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("AssetAssignments");
            migrationBuilder.DropTable("AssetDocuments");
            migrationBuilder.DropTable("AssetMaintenanceJobs");
            migrationBuilder.DropTable("AssetMeterReadings");
            migrationBuilder.DropColumn("AcquisitionCostUsd", "OperationalAssets");
            migrationBuilder.DropColumn("AcquisitionDate", "OperationalAssets");
            migrationBuilder.DropColumn("DisposalDate", "OperationalAssets");
            migrationBuilder.DropColumn("InServiceDate", "OperationalAssets");
            migrationBuilder.DropColumn("OperationalStatus", "OperationalAssets");
        }
    }
}
