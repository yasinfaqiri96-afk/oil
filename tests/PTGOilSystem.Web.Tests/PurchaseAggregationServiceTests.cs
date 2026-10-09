using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class PurchaseAggregationServiceTests
{
    [Fact]
    public async Task Group_Loading_Expense_Does_Not_Hide_Independent_Legacy_Freight()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", IsActive = true });
        db.LoadingRegisters.Add(new LoadingRegister { Id = 1, ContractId = 10, ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1), LoadedQuantityMt = 30m,
            LoadingPriceUsd = 500m, TransportExpenseUsd = 60m });
        db.ExpenseBatches.Add(new ExpenseBatch { Id = 1, ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 5, 2),
            TotalAmount = 70m, TotalAmountUsd = 70m, OperationCount = 1 });
        db.ExpenseTransactions.Add(new ExpenseTransaction { ExpenseTypeId = 1, ContractId = 10,
            LoadingRegisterId = 1, ExpenseBatchId = 1, ExpenseDate = new DateTime(2026, 5, 2),
            Amount = 70m, AmountUsd = 70m, Currency = "USD" });
        await db.SaveChangesAsync();
        var snapshot = await new PurchaseAggregationService(db).AggregateForContractAsync(10, null);
        Assert.Equal(60m, snapshot.LoadingTransportExpenseUsd);
        Assert.Equal(15000m, snapshot.TraceablePurchaseCostUsd);
        Assert.Equal(70m, await db.ExpenseTransactions.SumAsync(e => e.AmountUsd));
    }

    [Fact]
    public async Task PurchaseAggregationService_Returns_Current_LoadingRegister_Sums()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LoadingRegisters.AddRange(
            new LoadingRegister
            {
                Id = 1,
                ContractId = 10,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 1),
                LoadedQuantityMt = 32.5m,
                LoadingPriceUsd = 570m,
                TransportExpenseUsd = 45m,
                WarehouseExpenseUsd = 12m,
                OtherExpenseUsd = 3m,
                RailwayExpenseUsd = 7m
            },
            new LoadingRegister
            {
                Id = 2,
                ContractId = 10,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 2),
                LoadedQuantityMt = 31m,
                LoadingPriceUsd = 572m,
                TransportExpenseUsd = 44m,
                WarehouseExpenseUsd = 11m,
                OtherExpenseUsd = 4m,
                RailwayExpenseUsd = 8m
            },
            new LoadingRegister
            {
                Id = 3,
                ContractId = 10,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 3),
                LoadedQuantityMt = 10m,
                LoadingPriceUsd = null,
                TransportExpenseUsd = 5m,
                WarehouseExpenseUsd = 2m,
                OtherExpenseUsd = 1m,
                RailwayExpenseUsd = 2m
            },
            new LoadingRegister
            {
                Id = 4,
                ContractId = 11,
                ProductId = 1,
                LoadingDate = new DateTime(2026, 5, 4),
                LoadedQuantityMt = 4.5m,
                LoadingPriceUsd = null,
                TransportExpenseUsd = 9m,
                WarehouseExpenseUsd = 8m,
                OtherExpenseUsd = 7m,
                RailwayExpenseUsd = 6m
            });
        await db.SaveChangesAsync();

        var service = new PurchaseAggregationService(db);

        var priced = await service.AggregateForContractAsync(10, contractFinalPriceUsd: 575m);
        var pending = await service.AggregateForContractAsync(11, contractFinalPriceUsd: null);

        Assert.Equal(73.5m, priced.TotalLoadedQuantityMt);
        Assert.Equal(73.5m, priced.PricedPurchaseQuantityMt);
        Assert.Equal(0m, priced.PendingPurchaseQuantityMt);
        Assert.Equal(0, priced.PendingLoadingCount);
        Assert.Equal(42_007m, priced.TraceablePurchaseCostUsd);
        Assert.Equal(571.5238m, priced.WeightedAveragePurchasePriceUsd);
        Assert.Equal(94m, priced.LoadingTransportExpenseUsd);
        Assert.Equal(25m, priced.LoadingWarehouseExpenseUsd);
        Assert.Equal(8m, priced.LoadingOtherExpenseUsd);
        Assert.Equal(17m, priced.LoadingRailwayExpenseUsd);

        Assert.Equal(4.5m, pending.TotalLoadedQuantityMt);
        Assert.Equal(0m, pending.PricedPurchaseQuantityMt);
        Assert.Equal(4.5m, pending.PendingPurchaseQuantityMt);
        Assert.Equal(1, pending.PendingLoadingCount);
        Assert.Equal(0m, pending.TraceablePurchaseCostUsd);
        Assert.Null(pending.WeightedAveragePurchasePriceUsd);
    }

    [Fact]
    public void PurchaseAggregationService_Documents_Future_Transport_Leg_Guard()
    {
        var interfacePath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "PTGOilSystem.Web",
            "Services",
            "IPurchaseAggregationService.cs");
        var contents = File.ReadAllText(Path.GetFullPath(interfacePath));

        Assert.Contains("InventoryTransportLeg", contents);
        Assert.Contains("MUST NOT", contents);
    }

    [Fact]
    public async Task InventoryTransportLeg_Does_Not_Affect_PurchaseAggregationService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 10,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-001",
            RwbNo = "RWB-001",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 25m,
            Status = InventoryTransportLegStatus.InTransit
        });
        await db.SaveChangesAsync();

        var service = new PurchaseAggregationService(db);

        var snapshot = await service.AggregateForContractAsync(10, contractFinalPriceUsd: null);
        var loadedByContract = await service.GetLoadedQuantityByContractAsync();

        Assert.Equal(10m, snapshot.TotalLoadedQuantityMt);
        Assert.Equal(10m, snapshot.PricedPurchaseQuantityMt);
        Assert.Equal(1_000m, snapshot.TraceablePurchaseCostUsd);
        Assert.Equal(10m, loadedByContract[10]);
    }

    [Fact]
    public async Task TransportLeg_Expense_Does_Not_Affect_PurchaseAggregationService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 10m,
            LoadingPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = 10,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 25m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "WGN", Name = "Wagon expense" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 10,
            TransportLegId = 1,
            ExpenseDate = new DateTime(2026, 5, 3),
            Amount = 500m,
            Currency = "USD",
            AmountUsd = 500m
        });
        await db.SaveChangesAsync();

        var service = new PurchaseAggregationService(db);

        var snapshot = await service.AggregateForContractAsync(10, contractFinalPriceUsd: null);

        Assert.Equal(10m, snapshot.TotalLoadedQuantityMt);
        Assert.Equal(10m, snapshot.PricedPurchaseQuantityMt);
        Assert.Equal(1_000m, snapshot.TraceablePurchaseCostUsd);
    }

    [Fact]
    public async Task Legacy_Loading_With_Official_Expense_Drops_Inline_Fields()
    {
        // Legacy behavior (no LoadingExpenseLines): a loading that has an official
        // ExpenseTransaction must NOT also count its inline fields (avoid double count).
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LOAD-TRANSPORT", Name = "T", Category = "Transport", IsActive = true });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 30m,
            LoadingPriceUsd = 500m,
            TransportExpenseUsd = 60m
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 10,
            LoadingRegisterId = 1,
            ExpenseDate = new DateTime(2026, 5, 1),
            Amount = 60m,
            AmountUsd = 60m,
            Currency = "USD"
        });
        await db.SaveChangesAsync();

        var snapshot = await new PurchaseAggregationService(db).AggregateForContractAsync(10, contractFinalPriceUsd: null);

        Assert.Equal(0m, snapshot.LoadingTransportExpenseUsd);
    }

    [Fact]
    public async Task LineBased_Loading_Keeps_None_Mirror_Even_With_Official_Expense()
    {
        // Row-based loading: the inline fields mirror only the "None" lines, which
        // never overlap with the official ServiceProvider ExpenseTransaction — so they
        // must be KEPT even though the loading also has an official expense.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LOAD-TRANSPORT", Name = "T", Category = "Transport", IsActive = true });
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 30m,
            LoadingPriceUsd = 500m,
            TransportExpenseUsd = 25m // mirror of a "None" line
        });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 10,
            LoadingRegisterId = 1,
            ExpenseDate = new DateTime(2026, 5, 1),
            Amount = 70m,
            AmountUsd = 70m,
            Currency = "USD"
        });
        db.LoadingExpenseLines.AddRange(
            new LoadingExpenseLine
            {
                Id = 1,
                LoadingRegisterId = 1,
                ExpenseTypeId = 1,
                CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                AmountUsd = 25m,
                PartyType = LoadingExpensePartyType.None
            },
            new LoadingExpenseLine
            {
                Id = 2,
                LoadingRegisterId = 1,
                ExpenseTypeId = 1,
                CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
                AmountUsd = 70m,
                PartyType = LoadingExpensePartyType.ServiceProvider,
                ServiceProviderId = 5,
                ExpenseTransactionId = 1
            });
        await db.SaveChangesAsync();

        var snapshot = await new PurchaseAggregationService(db).AggregateForContractAsync(10, contractFinalPriceUsd: null);

        // None mirror is kept; the official 70 is counted elsewhere (generalExpense), not here.
        Assert.Equal(25m, snapshot.LoadingTransportExpenseUsd);
    }

    [Fact]
    public async Task Railway_FromLines_Tracks_Only_LineBased_Inline_Railway()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LOAD-OTHER", Name = "O", Category = "Other", IsActive = true });
        // Line-based loading with an inline railway mirror.
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 1,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 30m,
            LoadingPriceUsd = 500m,
            RailwayExpenseUsd = 18m
        });
        db.LoadingExpenseLines.Add(new LoadingExpenseLine
        {
            Id = 1,
            LoadingRegisterId = 1,
            ExpenseTypeId = 1,
            CalculationMode = LoadingExpenseCalculationMode.FixedAmount,
            AmountUsd = 18m,
            PartyType = LoadingExpensePartyType.None
        });
        // Legacy loading with inline railway, no lines.
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 2,
            ContractId = 10,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 2),
            LoadedQuantityMt = 20m,
            LoadingPriceUsd = 500m,
            RailwayExpenseUsd = 9m
        });
        await db.SaveChangesAsync();

        var snapshot = await new PurchaseAggregationService(db).AggregateForContractAsync(10, contractFinalPriceUsd: null);

        Assert.Equal(27m, snapshot.LoadingRailwayExpenseUsd);
        Assert.Equal(18m, snapshot.LoadingRailwayExpenseUsdFromLines);
    }
}
