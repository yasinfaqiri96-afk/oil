using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests;

public sealed class LoadingCreateBatchingTests
{
    private readonly ITestOutputHelper _output;

    public LoadingCreateBatchingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class SaveChangesCountingInterceptor : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingLossWorkflow : ILossEventWorkflowService
    {
        public IReadOnlyList<LossEventSubmission> BatchSubmissions { get; private set; } = [];

        public LossEventComputation ComputeMetrics(decimal expectedQuantityMt, decimal actualQuantityMt, decimal toleranceQuantityMt)
            => new(expectedQuantityMt - actualQuantityMt, toleranceQuantityMt, 0m);

        public Task ValidateAsync(
            LossEventSubmission submission,
            Action<string, string> addError,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<LossEventWorkflowResult> CreateAsync(
            LossEventSubmission submission,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The loading create path must use the batch loss API.");

        public Task<IReadOnlyList<LossEventWorkflowResult>> CreateBatchAsync(
            IReadOnlyList<LossEventSubmission> submissions,
            CancellationToken ct = default)
        {
            BatchSubmissions = submissions;
            return Task.FromResult<IReadOnlyList<LossEventWorkflowResult>>([]);
        }
    }

    [Fact]
    public async Task Create_Many_Wagon_Rows_Persists_All_Rows_And_Audits()
    {
        const int rowCount = 30;
        var counter = new SaveChangesCountingInterceptor();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(counter)
                .Options);

        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-BULK",
            ContractType = ContractType.Purchase,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 7, 22),
            QuantityMt = 1000m
        });
        await db.SaveChangesAsync();
        var baseline = counter.Count;

        var lossWorkflow = new RecordingLossWorkflow();
        var controller = new LoadingController(
            db,
            new AuditService(db),
            NullLogger<LoadingController>.Instance,
            lossWorkflow)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        var rows = Enumerable.Range(1, rowCount)
            .Select(index => new LoadingCreateRowViewModel
            {
                RowKey = $"row_{index}",
                ContractId = 1,
                LoadingDate = new DateTime(2026, 7, 22),
                BillOfLadingNumber = $"RWB-{index:D4}",
                WagonNumber = $"WG-{index:D4}",
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = 100m,
                Loss = new StageLossCaptureInput
                {
                    Enabled = true,
                    Stage = LossEventStage.LoadingDifference,
                    QuantityMt = 1m,
                    ToleranceQuantityMt = 0.1m
                }
            })
            .ToList();
        var model = new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            // The browser posts only the visible preview page as controls; the compact
            // payload remains the source of truth for every imported row.
            Rows = [rows[0]],
            ImportedRowsJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var result = await controller.Create(model);
        var saveCount = counter.Count - baseline;

        _output.WriteLine($"Loading create: rows={rowCount}, save_changes={saveCount}");
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(rowCount, await db.LoadingRegisters.CountAsync());
        Assert.Equal(rowCount, await db.AuditLogs.CountAsync(a => a.EntityName == nameof(LoadingRegister)));
        Assert.Equal(rowCount, lossWorkflow.BatchSubmissions.Count);
        Assert.All(lossWorkflow.BatchSubmissions, submission => Assert.True(submission.LoadingRegisterId.GetValueOrDefault() > 0));
        Assert.True(saveCount <= 2, $"Expected at most two SaveChanges calls, observed {saveCount}.");
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) =>
            new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
