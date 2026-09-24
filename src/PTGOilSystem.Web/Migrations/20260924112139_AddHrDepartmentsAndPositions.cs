using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddHrDepartmentsAndPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "Employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PositionId",
                table: "Employees",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    DepartmentId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Positions_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DepartmentId",
                table: "Employees",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_PositionId",
                table: "Employees",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_IsActive",
                table: "Departments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_DepartmentId",
                table: "Positions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_IsActive",
                table: "Positions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Name",
                table: "Positions",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_Departments_DepartmentId",
                table: "Employees",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_Positions_PositionId",
                table: "Employees",
                column: "PositionId",
                principalTable: "Positions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // دادهٔ موجود: متنِ آزادِ «دپارتمان» و «وظیفه» به بخش/بستِ تعریف‌شده تبدیل می‌شود.
            // - هر متنِ متمایز (بعد از حذف فاصلهٔ اضافه و بدون حساسیت به حروف بزرگ/کوچک) یک بخش/بست
            //   با پرتکرارترین املای همان متن می‌سازد. تطبیقِ تقریبی یا ادغامِ نام‌های متفاوت انجام نمی‌شود.
            // - کارمند فقط به همان نامِ منطبق وصل می‌شود.
            // - ستون‌های متنیِ قدیمی (Department, JobTitle) دست نمی‌خورند.
            migrationBuilder.Sql("""
                INSERT INTO "Departments" ("Name", "IsActive", "CreatedAtUtc")
                SELECT DISTINCT ON (lower(x.n)) x.n, TRUE, now()
                FROM (
                    SELECT btrim("Department") AS n, COUNT(*) AS c
                    FROM "Employees"
                    WHERE "Department" IS NOT NULL AND btrim("Department") <> ''
                    GROUP BY btrim("Department")
                ) x
                ORDER BY lower(x.n), x.c DESC, x.n;

                UPDATE "Employees" e
                SET "DepartmentId" = d."Id"
                FROM "Departments" d
                WHERE e."DepartmentId" IS NULL
                  AND e."Department" IS NOT NULL
                  AND lower(btrim(e."Department")) = lower(d."Name");

                INSERT INTO "Positions" ("Name", "IsActive", "CreatedAtUtc")
                SELECT DISTINCT ON (lower(x.n)) x.n, TRUE, now()
                FROM (
                    SELECT btrim("JobTitle") AS n, COUNT(*) AS c
                    FROM "Employees"
                    WHERE "JobTitle" IS NOT NULL AND btrim("JobTitle") <> ''
                    GROUP BY btrim("JobTitle")
                ) x
                ORDER BY lower(x.n), x.c DESC, x.n;

                UPDATE "Employees" e
                SET "PositionId" = p."Id"
                FROM "Positions" p
                WHERE e."PositionId" IS NULL
                  AND e."JobTitle" IS NOT NULL
                  AND lower(btrim(e."JobTitle")) = lower(p."Name");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_Departments_DepartmentId",
                table: "Employees");

            migrationBuilder.DropForeignKey(
                name: "FK_Employees_Positions_PositionId",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_Employees_DepartmentId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Employees_PositionId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PositionId",
                table: "Employees");
        }
    }
}
