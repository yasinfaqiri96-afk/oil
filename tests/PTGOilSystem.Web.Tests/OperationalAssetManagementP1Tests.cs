using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OperationalAssets;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class OperationalAssetManagementP1Tests
{
    [Fact]
    public async Task New_assignment_closes_previous_active_assignment_for_same_role()
    {
        await using var db = CreateDb();
        SeedAsset(db);
        db.Companies.AddRange(
            new Company { Id = 1, Code = "C1", Name = "Company 1", IsActive = true },
            new Company { Id = 2, Code = "C2", Name = "Company 2", IsActive = true });
        db.AssetAssignments.Add(new AssetAssignment
        {
            Id = 1, OperationalAssetId = 1, ResponsiblePartyType = AccountingPartyType.Company,
            ResponsiblePartyId = 1, Role = "مسئول اصلی", FromDate = Utc(2026, 1, 1)
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.AddAssignment(new AssetAssignmentCreateViewModel
        {
            OperationalAssetId = 1,
            ResponsiblePartyKey = $"{(int)AccountingPartyType.Company}:2",
            Role = "مسئول اصلی",
            FromDate = Utc(2026, 6, 1)
        });

        Assert.IsType<RedirectToActionResult>(result);
        var rows = await db.AssetAssignments.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(Utc(2026, 6, 1), rows[0].ToDate);
        Assert.Null(rows[1].ToDate);
        Assert.Equal(2, rows[1].ResponsiblePartyId);
    }

    [Fact]
    public async Task Profile_shows_maintenance_meter_and_document_expiry_without_financial_side_effects()
    {
        await using var db = CreateDb();
        SeedAsset(db);
        db.AssetMaintenanceJobs.Add(new AssetMaintenanceJob
        {
            OperationalAssetId = 1, JobType = AssetMaintenanceJobType.Repair,
            Status = AssetMaintenanceStatus.Completed, Title = "Brake repair", CompletedDate = Utc(2026, 5, 2)
        });
        db.AssetMeterReadings.Add(new AssetMeterReading
        {
            OperationalAssetId = 1, MeterType = AssetMeterType.OdometerKm,
            ReadingDate = Utc(2026, 5, 2), ReadingValue = 12500m
        });
        db.AssetDocuments.Add(new AssetDocument
        {
            OperationalAssetId = 1, DocumentType = AssetDocumentType.Insurance,
            OriginalFileName = "insurance.pdf", StoredFileName = "stored.pdf", FilePath = "/uploads/test.pdf",
            ExpiryDate = AfghanistanBusinessClock.SystemToday.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await BuildController(db).Details(1));
        var model = Assert.IsType<OperationalAssetProfileViewModel>(view.Model);
        Assert.Single(model.MaintenanceJobs);
        Assert.Single(model.MeterReadings);
        Assert.True(Assert.Single(model.Documents).IsExpired);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.JournalEntryLines.ToListAsync());
    }

    [Fact]
    public void Details_keeps_the_approved_eight_sections_and_explicit_antiforgery_forms()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PTGOilSystem.Web", "Views", "OperationalAssets", "Details.cshtml"));
        foreach (var tab in new[] { "overview", "ownership", "responsibility", "work", "costs", "income", "maintenance", "documents" })
            Assert.Contains($"{tab}|", view);
        Assert.Contains("asp-action=\"AddAssignment\"", view);
        Assert.Contains("asp-action=\"AddMaintenanceJob\"", view);
        Assert.Contains("asp-action=\"UploadDocument\"", view);
        Assert.True(view.Split("@Html.AntiForgeryToken()", StringSplitOptions.None).Length >= 7);
    }

    private static OperationalAssetsController BuildController(ApplicationDbContext db)
        => new(db) { TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()) };

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static void SeedAsset(ApplicationDbContext db)
        => db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1, AssetCode = "AS-1", Name = "Asset 1", AssetType = OperationalAssetType.Truck,
            OperationalStatus = OperationalAssetStatus.Active, IsActive = true
        });

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
