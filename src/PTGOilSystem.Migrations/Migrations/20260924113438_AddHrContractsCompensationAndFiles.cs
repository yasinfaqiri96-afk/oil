using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddHrContractsCompensationAndFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HrAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Owner = table.Column<int>(type: "integer", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HrAttachments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmploymentContracts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContractType = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: false),
                    EndDate = table.Column<DateTime>(type: "date", nullable: true),
                    DepartmentId = table.Column<int>(type: "integer", nullable: true),
                    PositionId = table.Column<int>(type: "integer", nullable: true),
                    BaseSalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WorkingDaysPerWeek = table.Column<decimal>(type: "numeric(4,1)", nullable: true),
                    WorkingHoursPerDay = table.Column<decimal>(type: "numeric(4,1)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttachmentId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TerminatedOn = table.Column<DateTime>(type: "date", nullable: true),
                    TerminationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RenewedFromContractId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmploymentContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmploymentContracts_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmploymentContracts_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmploymentContracts_EmploymentContracts_RenewedFromContract~",
                        column: x => x.RenewedFromContractId,
                        principalTable: "EmploymentContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmploymentContracts_HrAttachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "HrAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmploymentContracts_Positions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeCompensations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "date", nullable: true),
                    BaseSalary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SalaryType = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    EmploymentContractId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeCompensations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeCompensations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeCompensations_EmploymentContracts_EmploymentContrac~",
                        column: x => x.EmploymentContractId,
                        principalTable: "EmploymentContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCompensations_EmployeeId_EffectiveFrom",
                table: "EmployeeCompensations",
                columns: new[] { "EmployeeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCompensations_EmploymentContractId",
                table: "EmployeeCompensations",
                column: "EmploymentContractId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_AttachmentId",
                table: "EmploymentContracts",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_ContractNumber",
                table: "EmploymentContracts",
                column: "ContractNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_DepartmentId",
                table: "EmploymentContracts",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_EmployeeId_StartDate",
                table: "EmploymentContracts",
                columns: new[] { "EmployeeId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_PositionId",
                table: "EmploymentContracts",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_RenewedFromContractId",
                table: "EmploymentContracts",
                column: "RenewedFromContractId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentContracts_Status_EndDate",
                table: "EmploymentContracts",
                columns: new[] { "Status", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "UX_EmploymentContracts_OneActivePerEmployee",
                table: "EmploymentContracts",
                column: "EmployeeId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HrAttachments_EmployeeId",
                table: "HrAttachments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_HrAttachments_StoredFileName",
                table: "HrAttachments",
                column: "StoredFileName",
                unique: true);

            // تاریخچهٔ معاش از معاشِ پایهٔ فعلیِ هر کارمند شروع می‌شود (از تاریخِ شروعِ کار). معاشِ
            // قبل از آن ثبت نشده بود و ساخته نمی‌شود؛ ستونِ BaseSalaryAmount دست نمی‌خورد.
            migrationBuilder.Sql("""
                INSERT INTO "EmployeeCompensations"
                    ("EmployeeId", "EffectiveFrom", "EffectiveTo", "BaseSalary", "Currency", "SalaryType", "Source", "EmploymentContractId", "Notes", "CreatedAtUtc")
                SELECT e."Id", (e."HireDate" AT TIME ZONE 'UTC')::date, NULL, e."BaseSalaryAmount", e."SalaryCurrency", e."SalaryType", 3, NULL,
                       'مهاجرت از معاش پایهٔ ثبت‌شده در پروفایل کارمند', now()
                FROM "Employees" e
                WHERE e."BaseSalaryAmount" > 0
                  AND NOT EXISTS (SELECT 1 FROM "EmployeeCompensations" c WHERE c."EmployeeId" = e."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeCompensations");

            migrationBuilder.DropTable(
                name: "EmploymentContracts");

            migrationBuilder.DropTable(
                name: "HrAttachments");
        }
    }
}
