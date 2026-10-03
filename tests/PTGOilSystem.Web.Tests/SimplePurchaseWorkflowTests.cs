using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Profile «خرید ساده»: یک فورم خرید که در حالت Draft هیچ اثری ندارد و در حالت Confirmed
/// از همان زنجیرهٔ رسمی (LoadingRegister → LoadingReceipt(ToInventory) → InventoryMovement →
/// Supplier Ledger → Accounting Adapter) استفاده می‌کند. Reverse فقط از مسیر رسمی لغو رسید.
/// </summary>
public sealed class SimplePurchaseWorkflowTests
{
    private const int ProductId = 1;
    private const int TankId = 1;
    private const int TerminalId = 1;
    private const int SupplierId = 1;

    [Fact]
    public async Task Draft_Creates_No_Stock_Ledger_Or_Loading()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var controller = NewController(db, simplePurchaseEnabled: true);
        var result = await controller.Create(NewFormModel(ContractStatus.Draft), formToken: null);

        Assert.IsType<RedirectToActionResult>(result);
        var contract = await db.Contracts.SingleAsync();
        Assert.Equal(ContractStatus.Draft, contract.Status);
        Assert.Equal(TankId, contract.DestinationStorageTankId);
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.LoadingReceipts.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Confirm_Posts_Stock_And_Supplier_Debt_Exactly_Once_Into_Selected_Tank()
    {
        await using var db = NewDb();
        Seed(db);
        var contract = AddDraftContract(db, quantityMt: 40m, unitPriceUsd: 600m);
        await db.SaveChangesAsync();
        var accounting = new RecordingPurchaseAccounting();

        var result = await NewWorkflow(db, accounting).ConfirmAsync(contract.Id);

        Assert.True(result.Changed);
        Assert.Equal(ContractStatus.Active, (await db.Contracts.SingleAsync()).Status);

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(contract.Id, loading.ContractId);
        Assert.Equal(40m, loading.LoadedQuantityMt);
        Assert.Equal(600m, loading.LoadingPriceUsd);
        Assert.Equal(7, loading.OriginLocationId);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.Equal(LoadingReceiptDestination.ToInventory, receipt.ReceiptDestination);
        Assert.Equal(TankId, receipt.StorageTankId);
        Assert.Equal(TerminalId, receipt.TerminalId);
        Assert.Equal(40m, receipt.ReceivedQuantityMt);

        var movement = await db.InventoryMovements.SingleAsync();
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal(TankId, movement.StorageTankId);
        Assert.Equal(receipt.Id, movement.LoadingReceiptId);
        Assert.Equal(40m, movement.QuantityMt);

        var allocation = await db.LoadingReceiptAllocations.SingleAsync();
        Assert.Equal(LoadingReceiptAllocationDestination.ToInventory, allocation.Destination);
        Assert.Equal(LoadingReceiptAllocationStatus.Completed, allocation.Status);
        Assert.Equal(contract.Id, allocation.SourcePurchaseContractId);
        Assert.Equal(movement.Id, allocation.InventoryMovementId);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(SupplierLoadingLedger.SourceType, ledger.SourceType);
        Assert.Equal(loading.Id, ledger.SourceId);
        Assert.Equal(SupplierId, ledger.SupplierId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(24_000m, ledger.AmountUsd);

        Assert.Equal(40m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId));
        Assert.Equal(1, accounting.PurchasePosts);
        Assert.Equal(1, accounting.ReceiptPosts);
    }

    [Fact]
    public async Task Confirm_Twice_Does_Not_Duplicate_Stock_Or_Debt()
    {
        await using var db = NewDb();
        Seed(db);
        var contract = AddDraftContract(db, quantityMt: 40m, unitPriceUsd: 600m);
        await db.SaveChangesAsync();
        var accounting = new RecordingPurchaseAccounting();
        var workflow = NewWorkflow(db, accounting);

        var first = await workflow.ConfirmAsync(contract.Id);
        var second = await workflow.ConfirmAsync(contract.Id);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.LoadingRegisterId, second.LoadingRegisterId);
        Assert.Equal(first.LoadingReceiptId, second.LoadingReceiptId);
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
        Assert.Equal(1, await db.LoadingReceipts.CountAsync());
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
        Assert.Equal(40m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId));
        Assert.Equal(1, accounting.PurchasePosts);
        Assert.Equal(1, accounting.ReceiptPosts);
    }

    [Fact]
    public async Task Confirm_Rejects_Tank_Of_Another_Product()
    {
        await using var db = NewDb();
        Seed(db);
        db.Products.Add(new Product { Id = 2, Code = "MG", Name = "Mogas", IsActive = true });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = TerminalId, TankCode = "TK-02", ProductId = 2, IsActive = true });
        var contract = AddDraftContract(db, quantityMt: 10m, unitPriceUsd: 500m);
        contract.DestinationStorageTankId = 2;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => NewWorkflow(db, new RecordingPurchaseAccounting()).ConfirmAsync(contract.Id));

        Assert.Equal("SIMPLE_PURCHASE_TANK_PRODUCT_MISMATCH", ex.Code);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Reverse_Returns_Stock_Debt_And_Accounting_Exactly_Once()
    {
        await using var db = NewDb();
        Seed(db);
        var contract = AddDraftContract(db, quantityMt: 40m, unitPriceUsd: 600m);
        await db.SaveChangesAsync();
        var accounting = new RecordingPurchaseAccounting();
        var workflow = NewWorkflow(db, accounting);
        await workflow.ConfirmAsync(contract.Id);

        var reversed = await workflow.ReverseAsync(contract.Id, "خرید اشتباه ثبت شد");
        var again = await workflow.ReverseAsync(contract.Id, "دوباره");

        Assert.True(reversed.Changed);
        Assert.False(again.Changed);
        Assert.Equal(ContractStatus.Cancelled, (await db.Contracts.SingleAsync()).Status);

        var receipt = await db.LoadingReceipts.SingleAsync();
        Assert.True(receipt.IsCancelled);
        Assert.Equal("خرید اشتباه ثبت شد", receipt.CancellationReason);
        Assert.Equal(LoadingReceiptAllocationStatus.Cancelled, (await db.LoadingReceiptAllocations.SingleAsync()).Status);

        // سند اصلی حذف نمی‌شود؛ حرکت و سطر لجر معکوس اضافه می‌شود.
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId));

        var ledgers = await db.LedgerEntries.ToListAsync();
        Assert.Equal(2, ledgers.Count);
        var net = ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
        Assert.Equal(0m, net);

        Assert.Equal(1, accounting.ReceiptReversals);
        Assert.Equal(1, accounting.PurchaseReversals);
    }

    [Fact]
    public async Task Cancelled_Purchase_Cannot_Be_Confirmed_Again()
    {
        await using var db = NewDb();
        Seed(db);
        var contract = AddDraftContract(db, quantityMt: 40m, unitPriceUsd: 600m);
        await db.SaveChangesAsync();
        var workflow = NewWorkflow(db, new RecordingPurchaseAccounting());
        await workflow.ConfirmAsync(contract.Id);
        await workflow.ReverseAsync(contract.Id, "لغو");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => workflow.ConfirmAsync(contract.Id));

        Assert.Equal("SIMPLE_PURCHASE_ALREADY_POSTED", ex.Code);
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
        Assert.Equal(0m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId));
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("product")]
    [InlineData("tank")]
    [InlineData("draft")]
    public async Task Edit_Of_Confirmed_Purchase_Cannot_Change_Posted_Fields_Without_Reverse(string change)
    {
        await using var db = NewDb();
        Seed(db);
        db.Products.Add(new Product { Id = 2, Code = "MG", Name = "Mogas", IsActive = true, UnitId = 1 });
        db.StorageTanks.Add(new StorageTank { Id = 2, TerminalId = TerminalId, TankCode = "TK-02", IsActive = true });
        var contract = AddDraftContract(db, quantityMt: 40m, unitPriceUsd: 600m);
        await db.SaveChangesAsync();
        var workflow = NewWorkflow(db, new RecordingPurchaseAccounting());
        await workflow.ConfirmAsync(contract.Id);
        db.ChangeTracker.Clear();

        var stored = await db.Contracts.AsNoTracking().SingleAsync();
        var model = NewFormModel(ContractStatus.Active);
        model.Id = stored.Id;
        model.Version = stored.Version;
        model.ContractNumber = stored.ContractNumber;
        switch (change)
        {
            case "quantity": model.QuantityMt = 55m; break;
            case "product": model.ProductId = 2; break;
            case "tank": model.DestinationStorageTankId = 2; break;
            case "draft": model.Status = ContractStatus.Draft; break;
        }

        var controller = NewController(db, simplePurchaseEnabled: true, workflow);
        var result = await controller.Edit(stored.Id, model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("SimplePurchase", view.ViewName);
        Assert.False(controller.ModelState.IsValid);

        db.ChangeTracker.Clear();
        var after = await db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractStatus.Active, after.Status);
        Assert.Equal(40m, after.QuantityMt);
        Assert.Equal(ProductId, after.ProductId);
        Assert.Equal(TankId, after.DestinationStorageTankId);
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
        Assert.Equal(40m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: TerminalId, storageTankId: TankId));
    }

    [Fact]
    public async Task Create_Confirmed_Via_Controller_Posts_Once_And_Resubmitted_Confirm_Is_NoOp()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var workflow = NewWorkflow(db, new RecordingPurchaseAccounting());

        var first = await NewController(db, simplePurchaseEnabled: true, workflow)
            .Create(NewFormModel(ContractStatus.Active), formToken: null);
        db.ChangeTracker.Clear();

        // کلیک دوباره روی «تأیید» برای همان خرید: هیچ سند تازه‌ای ساخته نمی‌شود.
        var stored = await db.Contracts.AsNoTracking().SingleAsync();
        var resubmit = NewFormModel(ContractStatus.Active);
        resubmit.Id = stored.Id;
        resubmit.Version = stored.Version;
        resubmit.ContractNumber = stored.ContractNumber;
        var second = await NewController(db, simplePurchaseEnabled: true, workflow).Edit(stored.Id, resubmit);

        Assert.IsType<RedirectToActionResult>(first);
        Assert.IsType<RedirectToActionResult>(second);
        db.ChangeTracker.Clear();
        Assert.Equal(ContractStatus.Active, (await db.Contracts.SingleAsync()).Status);
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Disabled_Profile_Keeps_Original_Contract_Form_And_Posts_Nothing()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var workflow = new ThrowingWorkflow();

        var get = await NewController(db, simplePurchaseEnabled: false, workflow).Create(supplierId: null);
        var getView = Assert.IsType<ViewResult>(get);
        Assert.Equal("Create", getView.ViewName);
        Assert.Equal(ContractStatus.Active, Assert.IsType<ContractFormViewModel>(getView.Model).Status);

        // بدون مخزن و با وضعیت فعال: در حالت عادی هیچ قانون Profile اعمال نمی‌شود.
        var model = NewFormModel(ContractStatus.Active);
        model.DestinationStorageTankId = null;
        var post = await NewController(db, simplePurchaseEnabled: false, workflow).Create(model, formToken: null);

        Assert.IsType<RedirectToActionResult>(post);
        Assert.Equal(ContractStatus.Active, (await db.Contracts.SingleAsync()).Status);
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Enabled_Profile_Starts_New_Purchase_As_Draft_On_Simple_Form()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewController(db, simplePurchaseEnabled: true).Create(supplierId: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("SimplePurchase", view.ViewName);
        Assert.Equal(ContractStatus.Draft, Assert.IsType<ContractFormViewModel>(view.Model).Status);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", IsActive = true });
        db.Products.Add(new Product { Id = ProductId, Code = "GO", Name = "Gas Oil", IsActive = true, UnitId = 1 });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "Petro Trade Group", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = SupplierId, Code = "SUP", Name = "Supplier", IsActive = true });
        db.Locations.Add(new Location { Id = 7, Name = "Hairatan", IsActive = true });
        db.Terminals.Add(new Terminal { Id = TerminalId, Code = "TERM-1", Name = "Terminal", IsActive = true });
        db.StorageTanks.Add(new StorageTank
        {
            Id = TankId,
            TerminalId = TerminalId,
            TankCode = "TK-01",
            ProductId = ProductId,
            CapacityMt = 8000m,
            IsActive = true
        });
    }

    private static Contract AddDraftContract(ApplicationDbContext db, decimal quantityMt, decimal unitPriceUsd)
    {
        var contract = new Contract
        {
            Id = 10,
            ContractNumber = "P-0010",
            ContractName = "Simple purchase",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Draft,
            CompanyId = 1,
            ProductId = ProductId,
            UnitId = 1,
            SupplierId = SupplierId,
            PurchaseSourceLocationId = 7,
            DestinationStorageTankId = TankId,
            ContractDate = new DateTime(2026, 9, 1),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = quantityMt,
            Currency = "USD",
            UnitPriceInCurrency = unitPriceUsd,
            UnitPriceUsd = unitPriceUsd
        };
        db.Contracts.Add(contract);
        return contract;
    }

    private static ContractFormViewModel NewFormModel(ContractStatus status) => new()
    {
        ContractName = "Simple purchase",
        ContractNumber = "P-TEMP",
        ContractType = ContractType.Purchase,
        Status = status,
        CompanyId = 1,
        ProductId = ProductId,
        UnitId = 1,
        SupplierId = SupplierId,
        PurchaseSourceLocationId = 7,
        DestinationStorageTankId = TankId,
        OwnershipType = ContractOwnershipType.Personal,
        ContractDate = new DateTime(2026, 9, 1),
        PricingMethod = PricingMethod.Fixed,
        QuantityMt = 40m,
        Currency = "USD",
        SettlementCurrencyCode = "USD",
        RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
        UnitPriceInCurrency = 600m
    };

    private static SimplePurchaseWorkflowService NewWorkflow(
        ApplicationDbContext db,
        IPurchaseAccountingAdapter accounting)
    {
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

    private static ContractsController NewController(
        ApplicationDbContext db,
        bool simplePurchaseEnabled,
        ISimplePurchaseWorkflowService? workflow = null)
        => new(
            db,
            new AuditService(db),
            new CurrencyConversionService(new PricingService(db)),
            new FormTokenGuard(db),
            clientProfileOptions: Options.Create(new ClientProfileOptions { SimplePurchaseEnabled = simplePurchaseEnabled }),
            simplePurchaseWorkflow: workflow ?? NewWorkflow(db, new RecordingPurchaseAccounting()))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class ThrowingWorkflow : ISimplePurchaseWorkflowService
    {
        public Task<SimplePurchaseWorkflowResult> ConfirmAsync(int contractId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simple purchase must not run when the profile is disabled.");

        public Task<SimplePurchaseWorkflowResult> ReverseAsync(int contractId, string reason, int? actorUserId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Simple purchase must not run when the profile is disabled.");

        public Task<bool> HasPostingAsync(int contractId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simple purchase must not run when the profile is disabled.");
    }

    /// <summary>
    /// شمارندهٔ فراخوانی‌های Adapter حسابداری؛ اثر واقعی دفتر کل در تست PostgreSQL بررسی می‌شود.
    /// </summary>
    private sealed class RecordingPurchaseAccounting : IPurchaseAccountingAdapter
    {
        private static readonly PurchaseAccountingResult Skipped = new(PaymentPostingStatus.Skipped, null, "test");

        public int PurchasePosts { get; private set; }
        public int ReceiptPosts { get; private set; }
        public int PurchaseReversals { get; private set; }
        public int ReceiptReversals { get; private set; }

        public Task<PurchaseAccountingResult> TryPostPurchaseAsync(LoadingRegister loading, CancellationToken cancellationToken = default)
        {
            PurchasePosts++;
            return Task.FromResult(Skipped);
        }

        public Task<IReadOnlyList<PurchaseAccountingResult>> TryPostPurchasesAsync(IReadOnlyList<LoadingRegister> loadings, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(LoadingReceipt receipt, CancellationToken cancellationToken = default)
        {
            ReceiptPosts++;
            return Task.FromResult(Skipped);
        }

        public Task<PurchaseAccountingResult> TryPostTransportReceiptAsync(InventoryTransportReceipt receipt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(LoadingRegister loading, CancellationToken cancellationToken = default)
        {
            PurchaseReversals++;
            return Task.FromResult(Skipped);
        }

        public Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(LoadingReceipt receipt, CancellationToken cancellationToken = default)
        {
            ReceiptReversals++;
            return Task.FromResult(Skipped);
        }
    }
}
