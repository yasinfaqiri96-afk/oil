using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// migration <c>20260730165648_AddQualityInspections</c> فقط دو جدول جدید می‌سازد و هیچ
/// ستون یا جدول موجودی را Drop/Rename/Alter نمی‌کند. این تست همان migration را روی یک
/// دیتابیس موقت واقعی PostgreSQL اجرا می‌کند (fixture کل زنجیرهٔ migration را می‌سازد)
/// و CRUD کامل را می‌آزماید. هیچ دیتابیس توسعه یا تولیدی لمس نمی‌شود.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class QualityInspectionSchemaPostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task QualityInspection_Tables_Support_Full_Crud_On_Real_Schema()
    {
        await using var db = fixture.CreateDbContext();

        var product = new Product
        {
            Code = $"QI-{Guid.NewGuid():N}"[..12],
            Name = "Quality Test Product"
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var inspection = new QualityInspection
        {
            ProductId = product.Id,
            LaboratoryName = "SGS Kabul",
            ResultNumber = "LAB-001",
            SampleDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
            Status = QualityInspectionStatus.Pending,
            DensityKgM3 = 835.1234m,
            SulphurPercent = 0.001234m,
            Documents =
            {
                new QualityInspectionDocument
                {
                    OriginalFileName = "result.pdf",
                    StoredFileName = "result-1.pdf",
                    FilePath = "uploads/quality-inspections/1/result-1.pdf",
                    ContentType = "application/pdf",
                    FileSizeBytes = 2048,
                    UploadedAt = new DateTime(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        db.QualityInspections.Add(inspection);
        await db.SaveChangesAsync();

        // Read
        var reloaded = await db.QualityInspections
            .Include(q => q.Documents)
            .AsNoTracking()
            .SingleAsync(q => q.Id == inspection.Id);
        Assert.Equal(QualityInspectionStatus.Pending, reloaded.Status);
        Assert.Equal(835.1234m, reloaded.DensityKgM3);
        Assert.Single(reloaded.Documents);

        // Update: رد شدن آزمایش با دلیل.
        var tracked = await db.QualityInspections.SingleAsync(q => q.Id == inspection.Id);
        tracked.Status = QualityInspectionStatus.Rejected;
        tracked.RejectionReason = "گوگرد بالاتر از حد مجاز";
        tracked.ResultDate = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var afterUpdate = await db.QualityInspections.AsNoTracking()
            .SingleAsync(q => q.Id == inspection.Id);
        Assert.Equal(QualityInspectionStatus.Rejected, afterUpdate.Status);
        Assert.Equal("گوگرد بالاتر از حد مجاز", afterUpdate.RejectionReason);

        // Delete: سند پیوست باید آبشاری حذف شود.
        db.QualityInspections.Remove(tracked);
        await db.SaveChangesAsync();

        Assert.False(await db.QualityInspections.AnyAsync(q => q.Id == inspection.Id));
        Assert.False(await db.QualityInspectionDocuments
            .AnyAsync(d => d.QualityInspectionId == inspection.Id));
    }
}
