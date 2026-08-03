using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد روبلی با سیاست «نرخ هر بارگیری»: اگر فایل اکسل هم قیمت دالری و هم قیمت روبلی
/// داشته باشد، نرخ روبل/دالر باید از خود فایل مشتق شود و ثبت بدون نرخ دستی انجام شود.
/// </summary>
public sealed class LoadingImportRubRateDerivationTests
{
    [Fact]
    public async Task Create_Derives_Rub_Rate_From_File_Figures()
    {
        await using var db = BuildDatabase();
        var controller = BuildController(db);

        var rows = BuildRows(unitPriceRub: 48581.26m, totalRub: 2956169.671m);
        var result = await controller.Create(BuildModel(rows));

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await db.LoadingRegisters.ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, loading =>
        {
            Assert.Equal(RubSettlementRateStatus.Locked, loading.RubRateStatus);
            Assert.Equal(91.564107m, loading.RubPerUsdRate);
            Assert.Equal("Loading file", loading.RubRateSource);
            Assert.True(loading.AmountRubAtRubLock > 0m);
        });
    }

    [Fact]
    public async Task Create_Keeps_Asking_For_The_Rate_When_The_File_Figures_Are_Not_A_Rub_Price()
    {
        await using var db = BuildDatabase();
        var controller = BuildController(db);

        // ستون روبلیِ فایل در واقع قیمت فی‌تن روبلی نیست (کوچک‌تر از قیمت دالری است)؛
        // نرخ بی‌معنا نباید قفل شود.
        var rows = BuildRows(unitPriceRub: 76.89m, totalRub: 4551.876m);
        var result = await controller.Create(BuildModel(rows));

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(view.ViewData.ModelState.IsValid);
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
    }

    private static ApplicationDbContext BuildDatabase()
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2025, 12, 28),
            QuantityMt = 14170m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.PerLoadingRate
        });
        db.SaveChanges();
        return db;
    }

    private static LoadingController BuildController(ApplicationDbContext db)
        => new(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance,
            new NoopLossWorkflow())
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private static List<LoadingCreateRowViewModel> BuildRows(decimal unitPriceRub, decimal totalRub)
        => Enumerable.Range(1, 2)
            .Select(index => new LoadingCreateRowViewModel
            {
                RowKey = $"xls_{index}",
                ContractId = 1,
                LoadingDate = new DateTime(2025, 12, 28),
                BillOfLadingNumber = $"RWB-{index:D4}",
                WagonNumber = $"WG-{index:D4}",
                LoadedQuantityMt = 60.85m,
                LoadingPriceUsd = 530.5710m,
                SettlementUnitPriceRub = unitPriceRub,
                SettlementValueRub = totalRub,
                Loss = new StageLossCaptureInput { Stage = LossEventStage.LoadingDifference }
            })
            .ToList();

    private static LoadingCreateViewModel BuildModel(List<LoadingCreateRowViewModel> rows)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            Rows = [rows[0]],
            ImportedRowsJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

    private sealed class NoopLossWorkflow : ILossEventWorkflowService
    {
        public LossEventComputation ComputeMetrics(decimal expectedQuantityMt, decimal actualQuantityMt, decimal toleranceQuantityMt)
            => new(expectedQuantityMt - actualQuantityMt, toleranceQuantityMt, 0m);

        public Task ValidateAsync(LossEventSubmission submission, Action<string, string> addError, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<LossEventWorkflowResult> CreateAsync(LossEventSubmission submission, CancellationToken ct = default)
            => throw new InvalidOperationException("Not used by this test.");

        public Task<IReadOnlyList<LossEventWorkflowResult>> CreateBatchAsync(IReadOnlyList<LossEventSubmission> submissions, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LossEventWorkflowResult>>([]);
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
