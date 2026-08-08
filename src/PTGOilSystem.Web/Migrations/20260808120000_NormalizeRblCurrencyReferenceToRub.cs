using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations;

/// <summary>
/// Corrects the master-data typo only. Historical financial and operational
/// currency columns are intentionally not updated by this migration.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260808120000_NormalizeRblCurrencyReferenceToRub")]
public partial class NormalizeRblCurrencyReferenceToRub : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $migration$
            BEGIN
                IF EXISTS (SELECT 1 FROM "Currencies" WHERE upper("Code") = 'RBL') THEN
                    IF EXISTS (SELECT 1 FROM "Currencies" WHERE upper("Code") = 'RUB') THEN
                        UPDATE "Currencies"
                        SET "IsActive" = FALSE
                        WHERE upper("Code") = 'RBL';
                    ELSE
                        UPDATE "Currencies"
                        SET "Code" = 'RUB'
                        WHERE upper("Code") = 'RBL';
                    END IF;
                END IF;
            END
            $migration$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately irreversible: reintroducing the invalid ISO code RBL
        // would corrupt reference data. No historical rows were changed in Up.
    }
}
