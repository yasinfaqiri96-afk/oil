using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// خرید ساده روی PostgreSQL واقعی با Adapter حسابداری فعال: Confirm دقیقاً یک سند خرید و یک سند
/// رسید می‌سازد و Average Cost را حرکت می‌دهد؛ Reverse هر دو سند و استخر میانگین را برمی‌گرداند.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class SimplePurchaseAccountingTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime PurchaseDate = new(2026, 7, 5);

    [Fact]
    public async Task Confirm_Posts_Journals_And_Average_Cost_Once_And_Reverse_Returns_Them()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var contract = new Contract
        {
            ContractNumber = PaymentAccountingAdapterTests.Unique("SP"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = scope.Company.Id,
            ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id,
            DestinationStorageTankId = scope.Tank.Id,
            ContractDate = PurchaseDate,
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 20m,
            Currency = "USD",
            SettlementCurrencyCode = "USD",
            UnitPriceInCurrency = 500m,
            UnitPriceUsd = 500m
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        var workflow = NewWorkflow(db);

        var first = await workflow.ConfirmAsync(contract.Id);
        var second = await workflow.ConfirmAsync(contract.Id);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        var purchaseEventPrefix = $"Purchase:{first.LoadingRegisterId}:Created:";
        var receiptEvent = PurchaseAccountingAdapter.BuildReceiptSourceEventId(first.LoadingReceiptId);
        Assert.Equal(1, await db.JournalEntries.CountAsync(x => x.SourceEventId!.StartsWith(purchaseEventPrefix)));
        Assert.Equal(1, await db.JournalEntries.CountAsync(x => x.SourceEventId == receiptEvent));

        var pool = await LoadPoolAsync(db, scope);
        Assert.Equal(20m, pool.QuantityMt);
        Assert.Equal(10_000m, pool.TotalValueUsd);
        Assert.Equal(500m, pool.TotalValueUsd / pool.QuantityMt);
        Assert.Equal(1, await db.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierLoadingLedger.SourceType && x.SourceId == first.LoadingRegisterId));

        var reversed = await workflow.ReverseAsync(contract.Id, "خرید اشتباه");

        Assert.True(reversed.Changed);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PurchaseAccountingAdapter.BuildReceiptReversedSourceEventId(first.LoadingReceiptId)));
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId!.StartsWith($"Purchase:{first.LoadingRegisterId}:Reversed:")));

        db.ChangeTracker.Clear();
        pool = await LoadPoolAsync(db, scope);
        Assert.Equal(0m, pool.QuantityMt);
        Assert.Equal(0m, pool.TotalValueUsd);

        var supplierLedger = await db.LedgerEntries
            .Where(x => x.SupplierId == scope.Supplier.Id && x.ContractId == contract.Id)
            .ToListAsync();
        Assert.Equal(2, supplierLedger.Count);
        Assert.Equal(0m, supplierLedger.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd));
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(
            scope.Product.Id,
            terminalId: scope.Terminal.Id,
            storageTankId: scope.Tank.Id));
    }

    private static Task<InventoryAverageCost> LoadPoolAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope)
        => db.InventoryAverageCosts.AsNoTracking().SingleAsync(x =>
            x.CompanyId == scope.Company.Id
            && x.ProductId == scope.Product.Id
            && x.TerminalId == scope.Terminal.Id);

    private static SimplePurchaseWorkflowService NewWorkflow(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions
            {
                Purchase = true,
                InventoryReceipt = true
            }
        });
        var accounting = new PurchaseAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PricingService(db),
            new InventoryValuationService(db),
            options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
        var audit = new AuditService(db);
        var stock = new StockService(db);
        var movements = new InventoryMovementWriter(db, stock);
        var cancellation = new LoadingReceiptCancellationService(
            db,
            audit,
            NullLogger<LoadingReceiptCancellationService>.Instance,
            stock,
            accounting,
            movements: movements);
        return new SimplePurchaseWorkflowService(
            db,
            movements,
            accounting,
            cancellation,
            audit,
            new LedgerPostingService(db));
    }
}
