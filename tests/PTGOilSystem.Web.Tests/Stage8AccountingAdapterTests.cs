using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Stage 8 — losses, shortage charges and the two sarraf settlement flows.
///
/// Three of these four mappings have no legacy ledger row to compare against, so these tests are
/// the only statement of what the numbers should be. They pin the amounts and the sides, not
/// merely that a journal appeared.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class Stage8AccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime EventDate = new(2026, 7, 5);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal("InventoryLoss:7:Created", InventoryLossAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal("InventoryLoss:7:Reversed", InventoryLossAccountingAdapter.BuildReversedSourceEventId(7));
        Assert.Equal("ShortageCharge:7:Created", ShortageChargeAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal("SarrafSettlement:7:Created:0", SarrafSettlementAccountingAdapter.BuildCreatedSourceEventId(7, 0));
        Assert.Equal("SarrafSettlement:7:Reversed:1", SarrafSettlementAccountingAdapter.BuildReversedSourceEventId(7, 1));
        Assert.Equal("ThreeWaySettlement:7:Created", ThreeWaySettlementAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal("ThreeWaySettlement:7:Reversed", ThreeWaySettlementAccountingAdapter.BuildReversedSourceEventId(7));
    }

    [Theory]
    [InlineData(LossEventStage.TankNaturalLoss, true)]
    [InlineData(LossEventStage.ManualAdjustment, true)]
    [InlineData(LossEventStage.TankFinalSettlement, true)]
    [InlineData(LossEventStage.ReceiptShortage, false)]
    [InlineData(LossEventStage.LoadingDifference, false)]
    [InlineData(LossEventStage.TransitLoss, false)]
    [InlineData(LossEventStage.DispatchShortage, false)]
    [InlineData(LossEventStage.SalesDifference, false)]
    [InlineData(LossEventStage.CustomsLoss, false)]
    public void Only_Inventory_Reducing_Stages_Are_Recognised(LossEventStage stage, bool expected)
        => Assert.Equal(expected, InventoryLossAccountingAdapter.IsInventoryReducingStage(stage));

    // ---- Inventory loss ----

    [Fact]
    public async Task Loss_Writes_Off_Stock_At_The_Moving_Average()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // 20 MT at 10,000 then 10 MT at 6,000 averages 533.333333 per MT.
        var valuation = new InventoryValuationService(db);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 20m, 10_000m);
        await valuation.ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 6_000m);

        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.TankNaturalLoss, quantityMt: 3m);

        var result = await CreateLossAdapter(db).TryPostLossAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        // round(3 x 533.333333, 4) = 1600.0000
        var expectedCost = 1_600m;
        Assert.Equal(expectedCost, result.Journal!.Lines.Sum(x => x.Debit));

        var debit = Assert.Single(result.Journal.Lines.Where(x => x.Debit > 0m));
        Assert.Equal(scope.Settings.InventoryLossAccountId, debit.AccountId);
        var credit = Assert.Single(result.Journal.Lines.Where(x => x.Credit > 0m));
        Assert.Equal(scope.Settings.InventoryAccountId, credit.AccountId);
        Assert.Equal(expectedCost, credit.Credit);

        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(27m, pool.QuantityMt);
        Assert.Equal(14_400m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Loss_Is_Idempotent_And_Consumes_The_Pool_Once()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.ManualAdjustment, quantityMt: 2m);
        var adapter = CreateLossAdapter(db);

        var first = await adapter.TryPostLossAsync(lossEvent);
        var second = await adapter.TryPostLossAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Posted, first.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);

        // The second attempt must not have taken a second bite out of the pool.
        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(8m, pool.QuantityMt);
        Assert.Equal(4_000m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Loss_Beyond_The_Pool_Is_Skipped_Without_Touching_It()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 1m, 500m);
        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.TankNaturalLoss, quantityMt: 5m);

        var result = await CreateLossAdapter(db).TryPostLossAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("INVENTORY_NOT_VALUED", result.Reason);

        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(1m, pool.QuantityMt);
        Assert.Equal(500m, pool.TotalValueUsd);
    }

    [Fact]
    public async Task Loss_On_A_Provenance_Only_Stage_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        // A receipt shortage is already recognised through the shortage charge.
        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.ReceiptShortage, quantityMt: 2m);

        var result = await CreateLossAdapter(db).TryPostLossAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("LOSS_STAGE_NOT_RECOGNISED", result.Reason);
    }

    [Fact]
    public async Task Cancelling_A_Loss_Reverses_It_And_Returns_The_Value()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        await new InventoryValuationService(db)
            .ApplyReceiptAsync(scope.Company.Id, scope.Product.Id, scope.Terminal.Id, 10m, 5_000m);
        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.TankNaturalLoss, quantityMt: 4m);
        var adapter = CreateLossAdapter(db);
        await adapter.TryPostLossAsync(lossEvent);

        var reversal = await adapter.TryPostLossReversalAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Posted, reversal.Status);
        Assert.Equal(2_000m, reversal.Journal!.Lines.Sum(x => x.Debit));

        var pool = await db.InventoryAverageCosts.AsNoTracking().SingleAsync(
            x => x.CompanyId == scope.Company.Id && x.TerminalId == scope.Terminal.Id);
        Assert.Equal(10m, pool.QuantityMt);
        Assert.Equal(5_000m, pool.TotalValueUsd);

        var again = await adapter.TryPostLossReversalAsync(lossEvent);
        Assert.Equal(PaymentPostingStatus.Duplicate, again.Status);
    }

    [Fact]
    public async Task Loss_Reversal_Without_An_Original_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var lossEvent = await AddLossEventAsync(db, scope, LossEventStage.TankNaturalLoss, quantityMt: 4m);

        var result = await CreateLossAdapter(db).TryPostLossReversalAsync(lossEvent);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("ORIGINAL_JOURNAL_NOT_POSTED", result.Reason);
    }

    // ---- Shortage charge ----

    [Fact]
    public async Task Shortage_Charges_The_Service_Provider_And_Offsets_The_Loss()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var receipt = await AddTransportReceiptAsync(db, scope, chargeUsd: 750m, useServiceProvider: true);

        var result = await CreateShortageAdapter(db).TryPostShortageChargeAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(750m, result.Journal!.Lines.Sum(x => x.Debit));

        var debit = Assert.Single(result.Journal.Lines.Where(x => x.Debit > 0m));
        Assert.Equal(scope.Settings.FreightPayableAccountId, debit.AccountId);
        Assert.Equal(AccountingPartyType.ServiceProvider, debit.PartyType);
        Assert.Equal(scope.ServiceProvider.Id, debit.PartyId);

        var credit = Assert.Single(result.Journal.Lines.Where(x => x.Credit > 0m));
        Assert.Equal(scope.Settings.InventoryLossAccountId, credit.AccountId);
        Assert.Equal(750m, credit.Credit);
    }

    [Fact]
    public async Task Shortage_Falls_Back_To_The_Driver_When_There_Is_No_Service_Provider()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var receipt = await AddTransportReceiptAsync(db, scope, chargeUsd: 300m, useServiceProvider: false);

        var result = await CreateShortageAdapter(db).TryPostShortageChargeAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var debit = Assert.Single(result.Journal!.Lines.Where(x => x.Debit > 0m));
        Assert.Equal(AccountingPartyType.Driver, debit.PartyType);
        Assert.Equal(scope.Driver.Id, debit.PartyId);
    }

    [Fact]
    public async Task Shortage_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var receipt = await AddTransportReceiptAsync(db, scope, chargeUsd: 120m, useServiceProvider: true);
        var adapter = CreateShortageAdapter(db);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostShortageChargeAsync(receipt)).Status);
        var second = await adapter.TryPostShortageChargeAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
    }

    [Fact]
    public async Task Shortage_On_A_Company_Truck_Is_Not_Charged()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var asset = new OperationalAsset
        {
            AssetCode = PaymentAccountingAdapterTests.Unique("OA"),
            Name = PaymentAccountingAdapterTests.Unique("Asset"),
            IsActive = true
        };
        db.OperationalAssets.Add(asset);
        await db.SaveChangesAsync();

        var receipt = await AddTransportReceiptAsync(
            db, scope, chargeUsd: 500m, useServiceProvider: true, operationalAssetId: asset.Id);

        var result = await CreateShortageAdapter(db).TryPostShortageChargeAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("OPERATIONAL_ASSET_NOT_CHARGED", result.Reason);
    }

    [Fact]
    public async Task Shortage_Without_A_Charge_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var receipt = await AddTransportReceiptAsync(db, scope, chargeUsd: null, useServiceProvider: true);

        var result = await CreateShortageAdapter(db).TryPostShortageChargeAsync(receipt);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("NO_SHORTAGE_CHARGE", result.Reason);
    }

    // ---- Sarraf settlement ----

    [Fact]
    public async Task Sarraf_Settlement_Balances_The_Counterparty_Against_What_The_Sarraf_Charged()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        // The supplier's claim falls by 1,000; the sarraf charged us 1,010, so the 10 is a loss.
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 1_000m, sarrafChargedUsd: 1_010m);

        var result = await CreateSarrafAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(1_010m, result.Journal!.Lines.Sum(x => x.Debit));
        Assert.Equal(1_010m, result.Journal.Lines.Sum(x => x.Credit));

        var supplierLine = Assert.Single(result.Journal.Lines.Where(x => x.PartyType == AccountingPartyType.Supplier));
        Assert.Equal(scope.Settings.AccountsPayableAccountId, supplierLine.AccountId);
        Assert.Equal(1_000m, supplierLine.Debit);

        var sarrafLine = Assert.Single(result.Journal.Lines.Where(x => x.PartyType == AccountingPartyType.Sarraf));
        Assert.Equal(scope.Settings.AccountsPayableAccountId, sarrafLine.AccountId);
        Assert.Equal(1_010m, sarrafLine.Credit);

        var lossLine = Assert.Single(result.Journal.Lines.Where(
            x => x.AccountId == scope.Settings.ExchangeLossAccountId));
        Assert.Equal(10m, lossLine.Debit);
    }

    [Fact]
    public async Task Sarraf_Settlement_Recognises_A_Gain_When_The_Sarraf_Charged_Less()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 1_000m, sarrafChargedUsd: 990m);

        var result = await CreateSarrafAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var gainLine = Assert.Single(result.Journal!.Lines.Where(
            x => x.AccountId == scope.Settings.ExchangeGainAccountId));
        Assert.Equal(10m, gainLine.Credit);
        Assert.Equal(1_000m, result.Journal.Lines.Sum(x => x.Debit));
    }

    [Fact]
    public async Task Sarraf_Settlement_Without_A_Gap_Posts_Two_Lines()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 800m, sarrafChargedUsd: 800m);

        var result = await CreateSarrafAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(2, result.Journal!.Lines.Count);
        Assert.Equal(800m, result.Journal.Lines.Sum(x => x.Debit));
    }

    [Fact]
    public async Task Sarraf_Settlement_Is_Idempotent()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 500m, sarrafChargedUsd: 505m);
        var adapter = CreateSarrafAdapter(db);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostSettlementAsync(settlement)).Status);
        var second = await adapter.TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
    }

    [Fact]
    public async Task Editing_A_Sarraf_Settlement_Reverses_The_Old_Revision_And_Posts_The_New()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 500m, sarrafChargedUsd: 500m);
        var adapter = CreateSarrafAdapter(db);
        await adapter.TryPostSettlementAsync(settlement);

        // The edit path reverses, changes the figures, then posts again.
        var reversal = await adapter.TryPostSettlementReversalAsync(settlement);
        Assert.Equal(PaymentPostingStatus.Posted, reversal.Status);

        settlement.SupplierAcceptedAmount = 700m;
        settlement.SupplierAcceptedAmountUsd = 700m;
        settlement.SarrafChargedAmount = 700m;
        settlement.SarrafChargedAmountUsd = 700m;
        await db.SaveChangesAsync();

        var reposted = await adapter.TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, reposted.Status);
        Assert.Equal(700m, reposted.Journal!.Lines.Sum(x => x.Debit));
        Assert.Contains(":Created:1", reposted.Journal.SourceEventId);

        // Revision 0 posted 500, was reversed by 500, and revision 1 posted 700: net 700.
        var supplierNet = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.JournalEntry!.CompanyId == scope.Company.Id
                && x.JournalEntry.SourceModule == SarrafSettlementAccountingAdapter.SourceModule
                && x.PartyType == AccountingPartyType.Supplier)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(700m, supplierNet);
    }

    [Fact]
    public async Task Cancelling_A_Sarraf_Settlement_Reverses_Every_Revision()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 400m, sarrafChargedUsd: 400m);
        var adapter = CreateSarrafAdapter(db);
        await adapter.TryPostSettlementAsync(settlement);
        await adapter.TryPostSettlementReversalAsync(settlement);
        await adapter.TryPostSettlementAsync(settlement);

        var cancellation = await adapter.TryPostSettlementReversalAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, cancellation.Status);
        var net = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.JournalEntry!.CompanyId == scope.Company.Id
                && x.JournalEntry.SourceModule == SarrafSettlementAccountingAdapter.SourceModule)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, net);

        var again = await adapter.TryPostSettlementReversalAsync(settlement);
        Assert.Equal(PaymentPostingStatus.Duplicate, again.Status);
    }

    [Fact]
    public async Task Sarraf_Settlement_With_An_Unprovable_Company_Is_Skipped()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 500m, sarrafChargedUsd: 500m);
        settlement.ContractId = null;
        settlement.CashAccountId = null;
        settlement.PaymentTransactionId = null;
        await db.SaveChangesAsync();

        var result = await CreateSarrafAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("SARRAF_COMPANY_UNKNOWN", result.Reason);
    }

    [Fact]
    public async Task Sarraf_Settlement_Whose_Stored_Conversion_Does_Not_Hold_Stays_Legacy_Only()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddSarrafSettlementAsync(db, scope, acceptedUsd: 500m, sarrafChargedUsd: 500m);
        // A USD amount that does not equal amount x rate cannot be posted without inventing a rate.
        settlement.SarrafChargedAmountUsd = 499m;
        await db.SaveChangesAsync();

        var result = await CreateSarrafAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("INVALID_SARRAF_CONVERSION", result.Reason);
    }

    // ---- Three-way settlement ----

    [Fact]
    public async Task Three_Way_Settlement_Settles_Both_Legs_With_No_Sarraf_Line()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        // The customer paid 1,000; the supplier only credited us 950, so the 50 is a loss.
        var settlement = await AddThreeWaySettlementAsync(db, scope, customerPaidUsd: 1_000m, supplierAcceptedUsd: 950m);

        var result = await CreateThreeWayAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(1_000m, result.Journal!.Lines.Sum(x => x.Debit));
        Assert.Equal(1_000m, result.Journal.Lines.Sum(x => x.Credit));

        var supplierLine = Assert.Single(result.Journal.Lines.Where(x => x.PartyType == AccountingPartyType.Supplier));
        Assert.Equal(scope.Settings.AccountsPayableAccountId, supplierLine.AccountId);
        Assert.Equal(950m, supplierLine.Debit);

        var customerLine = Assert.Single(result.Journal.Lines.Where(x => x.PartyType == AccountingPartyType.Customer));
        Assert.Equal(scope.Settings.AccountsReceivableAccountId, customerLine.AccountId);
        Assert.Equal(1_000m, customerLine.Credit);

        var lossLine = Assert.Single(result.Journal.Lines.Where(
            x => x.AccountId == scope.Settings.ExchangeLossAccountId));
        Assert.Equal(50m, lossLine.Debit);

        // The sarraf holds nothing once both legs settle at once.
        Assert.DoesNotContain(result.Journal.Lines, x => x.PartyType == AccountingPartyType.Sarraf);
    }

    [Fact]
    public async Task Three_Way_Settlement_Paid_Direct_To_The_Supplier_Is_Out_Of_Scope()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddThreeWaySettlementAsync(
            db, scope, customerPaidUsd: 500m, supplierAcceptedUsd: 500m, payeeType: ThreeWayPayeeType.Supplier);

        var result = await CreateThreeWayAdapter(db).TryPostSettlementAsync(settlement);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("UNSUPPORTED_PAYEE_TYPE", result.Reason);
    }

    [Fact]
    public async Task Three_Way_Settlement_Is_Idempotent_And_Reversible()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var settlement = await AddThreeWaySettlementAsync(db, scope, customerPaidUsd: 600m, supplierAcceptedUsd: 600m);
        var adapter = CreateThreeWayAdapter(db);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostSettlementAsync(settlement)).Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostSettlementAsync(settlement)).Status);

        var reversal = await adapter.TryPostSettlementReversalAsync(settlement);
        Assert.Equal(PaymentPostingStatus.Posted, reversal.Status);
        Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostSettlementReversalAsync(settlement)).Status);

        var net = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.JournalEntry!.CompanyId == scope.Company.Id
                && x.JournalEntry.SourceModule == ThreeWaySettlementAccountingAdapter.SourceModule)
            .SumAsync(x => x.Debit - x.Credit);
        Assert.Equal(0m, net);
    }

    // ---- helpers ----

    private static AccountingOptions EnabledOptions()
        => new()
        {
            Enabled = true,
            DefaultFunctionalCurrencyCode = "USD",
            Pilots = new AccountingPilotOptions
            {
                InventoryLoss = true,
                ShortageCharge = true,
                SarrafSettlement = true,
                ThreeWaySettlement = true
            }
        };

    private static AccountingPostingService CreatePostingService(ApplicationDbContext db)
        => new(db, new PeriodGuard(db, new FiscalCalendarService(db)), Options.Create(EnabledOptions()), new SystemCompanyProvider(db));

    private static InventoryLossAccountingAdapter CreateLossAdapter(ApplicationDbContext db)
        => new(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            new InventoryValuationService(db),
            Options.Create(EnabledOptions()),
            NullLogger<InventoryLossAccountingAdapter>.Instance);

    private static ShortageChargeAccountingAdapter CreateShortageAdapter(ApplicationDbContext db)
        => new(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            Options.Create(EnabledOptions()),
            NullLogger<ShortageChargeAccountingAdapter>.Instance);

    private static SarrafSettlementAccountingAdapter CreateSarrafAdapter(ApplicationDbContext db)
        => new(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            Options.Create(EnabledOptions()),
            NullLogger<SarrafSettlementAccountingAdapter>.Instance);

    private static ThreeWaySettlementAccountingAdapter CreateThreeWayAdapter(ApplicationDbContext db)
        => new(
            db,
            CreatePostingService(db),
            new AccountingJournalNumberGenerator(),
            Options.Create(EnabledOptions()),
            NullLogger<ThreeWaySettlementAccountingAdapter>.Instance);

    private static async Task<LossEvent> AddLossEventAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        LossEventStage stage,
        decimal quantityMt)
    {
        var movement = new InventoryMovement
        {
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            Direction = MovementDirection.Out,
            MovementDate = EventDate,
            QuantityMt = quantityMt,
            ReferenceDocument = PaymentAccountingAdapterTests.Unique("LOSS")
        };
        db.InventoryMovements.Add(movement);
        await db.SaveChangesAsync();

        var lossEvent = new LossEvent
        {
            Stage = stage,
            ProductId = scope.Product.Id,
            ContractId = scope.Contract.Id,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            EventDate = EventDate,
            ExpectedQuantityMt = quantityMt,
            ActualQuantityMt = 0m,
            DifferenceQuantityMt = quantityMt,
            AffectsInventory = true,
            InventoryMovementId = movement.Id
        };
        db.LossEvents.Add(lossEvent);
        await db.SaveChangesAsync();
        return lossEvent;
    }

    private static async Task<InventoryTransportReceipt> AddTransportReceiptAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal? chargeUsd,
        bool useServiceProvider,
        int? operationalAssetId = null)
    {
        var leg = new InventoryTransportLeg
        {
            SourcePurchaseContractId = scope.Contract.Id,
            ProductId = scope.Product.Id,
            SourceTerminalId = scope.Terminal.Id,
            TransportType = LoadingTransportType.Truck,
            DriverId = useServiceProvider ? null : scope.Driver.Id,
            ServiceProviderId = useServiceProvider ? scope.ServiceProvider.Id : null,
            LoadedDate = EventDate,
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Draft
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();

        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = EventDate,
            ReceivedQuantityMt = 19m,
            ShortageQuantityMt = 1m,
            ChargeableShortageMt = 1m,
            ShortageChargeUsd = chargeUsd,
            ServiceProviderId = useServiceProvider ? scope.ServiceProvider.Id : null,
            OperationalAssetId = operationalAssetId
        };
        db.InventoryTransportReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<SarrafSettlement> AddSarrafSettlementAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal acceptedUsd,
        decimal sarrafChargedUsd)
    {
        var settlement = new SarrafSettlement
        {
            SettlementDate = EventDate,
            Direction = SarrafSettlementDirection.Out,
            CounterpartyType = SarrafSettlementCounterpartyType.Supplier,
            SarrafId = scope.Sarraf.Id,
            SupplierId = scope.Supplier.Id,
            ContractId = scope.Contract.Id,
            RequestedAmount = acceptedUsd,
            RequestedCurrency = "USD",
            RequestedFxRateToUsd = 1m,
            RequestedAmountUsd = acceptedUsd,
            SarrafCurrency = "USD",
            SarrafRate = 1m,
            SarrafChargedAmount = sarrafChargedUsd,
            SarrafFxRateToUsd = 1m,
            SarrafChargedAmountUsd = sarrafChargedUsd,
            SupplierAcceptedAmount = acceptedUsd,
            SupplierAcceptedCurrency = "USD",
            SupplierAcceptedFxRateToUsd = 1m,
            SupplierAcceptedAmountUsd = acceptedUsd,
            DifferenceAmountUsd = 0m,
            DifferenceType = SarrafSettlementDifferenceType.None,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Status = SarrafSettlementStatus.Posted,
            PostedAtUtc = DateTime.UtcNow
        };
        db.SarrafSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }

    private static async Task<ThreeWaySettlement> AddThreeWaySettlementAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal customerPaidUsd,
        decimal supplierAcceptedUsd,
        ThreeWayPayeeType payeeType = ThreeWayPayeeType.Sarraf)
    {
        var settlement = new ThreeWaySettlement
        {
            SettlementDate = EventDate,
            PayeeType = payeeType,
            Status = ThreeWaySettlementStatus.Posted,
            CustomerId = scope.Customer.Id,
            SupplierId = scope.Supplier.Id,
            SarrafId = payeeType == ThreeWayPayeeType.Sarraf ? scope.Sarraf.Id : null,
            CustomerPaidAmount = customerPaidUsd,
            SupplierAcceptedAmount = supplierAcceptedUsd,
            Currency = "USD",
            FxRateToUsd = 1m,
            CustomerPaidCurrency = "USD",
            CustomerPaidFxRateToUsd = 1m,
            SupplierAcceptedCurrency = "USD",
            SupplierAcceptedFxRateToUsd = 1m,
            CustomerPaidUsd = customerPaidUsd,
            SupplierAcceptedUsd = supplierAcceptedUsd,
            DifferenceUsd = customerPaidUsd - supplierAcceptedUsd,
            SupplierPurchaseContractId = scope.Contract.Id,
            PostedAtUtc = DateTime.UtcNow
        };
        db.ThreeWaySettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }
}
