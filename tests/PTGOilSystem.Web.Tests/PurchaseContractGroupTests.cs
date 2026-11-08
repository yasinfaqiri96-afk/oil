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
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد خرید چندتأمین‌کننده: هر ردیف یک قرارداد مستقل است و همان زنجیرهٔ رسمی بارگیری، رسید،
/// موجودی و دفتر کل را طی می‌کند؛ سرِبرگ هیچ سند مالی یا موجودی ندارد.
/// </summary>
public sealed class PurchaseContractGroupTests
{
    private const int Akhund = 1;
    private const int Mahmood = 2;
    private const int ProductId = 1;
    private const int TankId = 1;

    [Fact]
    public async Task Standard_Contract_Keeps_Its_Behavior_And_Has_No_Group()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(new ContractFormViewModel
        {
            ContractName = "عادی",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = ProductId,
            UnitId = 1,
            SupplierId = Akhund,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 9, 1),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 40m,
            Currency = "USD",
            UnitPriceInCurrency = 600m
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ContractsController.Details), redirect.ActionName);
        var contract = await db.Contracts.SingleAsync();
        Assert.Null(contract.PurchaseContractGroupId);
        Assert.Equal(600m, contract.UnitPriceUsd);
        Assert.Empty(await db.PurchaseContractGroups.ToListAsync());
    }

    [Fact]
    public async Task Multi_Supplier_Creates_One_Group_And_Independent_Lines_Without_Any_Posting()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var result = await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ContractsController.PurchaseGroup), redirect.ActionName);
        var group = await db.PurchaseContractGroups.Include(g => g.Lines).SingleAsync();
        Assert.Equal("P-001", group.GroupNumber);
        Assert.Equal(2, group.Lines.Count);

        var akhund = group.Lines.Single(c => c.SupplierId == Akhund);
        Assert.Equal(100m, akhund.QuantityMt);
        Assert.Equal(600m, akhund.UnitPriceUsd);
        Assert.Equal(PricingMethod.Fixed, akhund.PricingMethod);
        Assert.Equal(600m, ContractPricingAdapter.GetCanonicalFinalPrice(akhund));
        Assert.Contains("Akhund", akhund.ContractName);
        var mahmood = group.Lines.Single(c => c.SupplierId == Mahmood);
        Assert.Equal(200m, mahmood.QuantityMt);
        Assert.Equal(500m, mahmood.UnitPriceUsd);
        Assert.NotEqual(akhund.Id, mahmood.Id);
        Assert.NotEqual(akhund.ContractNumber, mahmood.ContractNumber);

        // ثبت قرارداد تعهد است، نه خرید قطعی.
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
    }

    [Fact]
    public void Totals_Match_The_Requested_Example_And_Average_Is_Weighted()
    {
        var totals = PurchaseGroupTotals.Calculate(
        [
            new PurchaseGroupTotalsInput(Akhund, 100m, 600m),
            new PurchaseGroupTotalsInput(Mahmood, 200m, 500m)
        ]);

        Assert.Equal(300m, totals.TotalQuantityMt);
        Assert.Equal(160_000m, totals.TotalValueUsd);
        Assert.Equal(2, totals.SupplierCount);
        Assert.Equal(533.3333m, totals.WeightedAveragePriceUsd);
        Assert.Equal(0, totals.UnpricedLineCount);
    }

    [Fact]
    public void Unpriced_Line_Counts_In_Quantity_But_Not_In_Value_Or_Average()
    {
        var totals = PurchaseGroupTotals.Calculate(
        [
            new PurchaseGroupTotalsInput(Akhund, 100m, 600m),
            new PurchaseGroupTotalsInput(Mahmood, 200m, null)
        ]);

        Assert.Equal(300m, totals.TotalQuantityMt);
        Assert.Equal(60_000m, totals.TotalValueUsd);
        Assert.Equal(600m, totals.WeightedAveragePriceUsd);
        Assert.Equal(1, totals.UnpricedLineCount);
    }

    [Fact]
    public async Task Same_Supplier_Can_Have_Two_Lines_With_Different_Prices()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Akhund, 50m, 650m)));

        var lines = await db.Contracts.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(Akhund, line.SupplierId));
        Assert.Equal([600m, 650m], lines.Select(line => line.UnitPriceUsd!.Value));
        var totals = PurchaseGroupTotals.Calculate(lines.Select(l => new PurchaseGroupTotalsInput(l.SupplierId, l.QuantityMt, l.UnitPriceUsd)));
        Assert.Equal(1, totals.SupplierCount);
        Assert.Equal(150m, totals.TotalQuantityMt);
    }

    [Fact]
    public async Task Non_Usd_Line_Is_Converted_And_Keeps_The_Original_Price()
    {
        await using var db = NewDb();
        Seed(db);
        db.Currencies.Add(new Currency { Id = 2, Code = "AED", Name = "Dirham", IsActive = true });
        await db.SaveChangesAsync();
        var model = NewMultiModel(Line(Akhund, 100m, 2000m), Line(Mahmood, 10m, 1000m));
        model.Currency = "AED";
        model.AppliedFxRateToUsd = 0.25m;

        await NewController(db).Create(model);

        var line = await db.Contracts.SingleAsync(c => c.SupplierId == Akhund);
        Assert.Equal("AED", line.Currency);
        Assert.Equal(2000m, line.UnitPriceInCurrency);
        Assert.Equal(0.25m, line.AppliedFxRateToUsd);
        Assert.Equal(500m, line.UnitPriceUsd);
    }

    [Fact]
    public async Task Invalid_Lines_Show_Field_Errors_And_Nothing_Is_Saved()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var controller = NewController(db);

        var result = await controller.Create(NewMultiModel(
            new PurchaseContractLineInput { ProductId = ProductId, QuantityMt = 0m, UnitPrice = 500m },
            Line(Mahmood, 10m, 0m),
            new PurchaseContractLineInput()));

        Assert.IsType<ViewResult>(result);
        Assert.Contains("PurchaseLines[0].SupplierId", controller.ModelState.Keys);
        Assert.Contains("PurchaseLines[0].QuantityMt", controller.ModelState.Keys);
        Assert.Contains("PurchaseLines[1].UnitPrice", controller.ModelState.Keys);
        Assert.DoesNotContain(controller.ModelState.Keys, key => key.StartsWith("PurchaseLines[2]"));
        Assert.Empty(await db.Contracts.ToListAsync());
        Assert.Empty(await db.PurchaseContractGroups.ToListAsync());
    }

    [Fact]
    public async Task One_Line_Is_Rejected_And_Inactive_Supplier_Is_Rejected()
    {
        await using var db = NewDb();
        Seed(db);
        db.Suppliers.Add(new Supplier { Id = 9, Name = "Inactive", IsActive = false });
        await db.SaveChangesAsync();

        var oneLine = NewController(db);
        Assert.IsType<ViewResult>(await oneLine.Create(NewMultiModel(Line(Akhund, 100m, 600m))));
        Assert.Contains(nameof(ContractFormViewModel.PurchaseLines), oneLine.ModelState.Keys);

        var inactive = NewController(db);
        Assert.IsType<ViewResult>(await inactive.Create(NewMultiModel(Line(9, 100m, 600m), Line(Akhund, 1m, 1m))));
        Assert.Contains("PurchaseLines[0].SupplierId", inactive.ModelState.Keys);
        Assert.Empty(await db.Contracts.ToListAsync());
    }

    [Fact]
    public async Task Multi_Supplier_Is_Rejected_On_The_Simple_Purchase_Profile()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var controller = NewController(db, simplePurchaseEnabled: true);

        Assert.IsType<ViewResult>(await controller.Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 10m, 500m))));

        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.Contracts.ToListAsync());
    }

    [Fact]
    public async Task Form_Token_Is_Stored_Atomically_With_The_Group()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 10m, 500m)), "token-1");

        var token = await db.ProcessedFormTokens.SingleAsync();
        Assert.Equal("token-1", token.Token);
        Assert.Equal(nameof(PurchaseContractGroup), token.ReferenceType);
    }

    [Fact]
    public async Task Partner_Shares_Are_Copied_To_Every_Line()
    {
        await using var db = NewDb();
        Seed(db);
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "A", Name = "A", IsActive = true },
            new Partner { Id = 2, Code = "B", Name = "B", IsActive = true });
        await db.SaveChangesAsync();
        var model = NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 10m, 500m));
        model.OwnershipType = ContractOwnershipType.Partnership;
        model.PartnerShares =
        [
            new ContractPartnerShareInput { PartnerId = 1, SharePercent = 60m },
            new ContractPartnerShareInput { PartnerId = 2, SharePercent = 40m }
        ];

        await NewController(db).Create(model);

        foreach (var line in await db.Contracts.Include(c => c.ContractPartners).ToListAsync())
        {
            Assert.Equal(2, line.ContractPartners.Count);
            Assert.Equal(100m, line.ContractPartners.Sum(p => p.SharePercent));
            Assert.All(line.ContractPartners, p => Assert.Equal(line.ContractDate.Date, p.EffectiveFrom));
        }
    }

    [Fact]
    public async Task Confirming_Lines_Posts_Stock_And_Debt_Once_To_The_Right_Supplier()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        var draft = NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m));
        draft.Status = ContractStatus.Draft;
        await NewController(db).Create(draft);
        var lines = await db.Contracts.OrderBy(c => c.Id).ToListAsync();
        foreach (var line in lines)
        {
            line.DestinationStorageTankId = TankId;
            line.PurchaseSourceLocationId = 7;
        }
        await db.SaveChangesAsync();
        var workflow = NewWorkflow(db);

        await workflow.ConfirmAsync(lines[0].Id);
        await workflow.ConfirmAsync(lines[1].Id);
        var repeated = await workflow.ConfirmAsync(lines[0].Id); // تکرار نباید سند تازه بسازد.

        Assert.False(repeated.Changed);

        var ledger = await db.LedgerEntries.OrderBy(l => l.SupplierId).ToListAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Equal((Akhund, lines[0].Id, 60_000m, LedgerSide.Credit), (ledger[0].SupplierId!.Value, ledger[0].ContractId!.Value, ledger[0].AmountUsd, ledger[0].Side));
        Assert.Equal((Mahmood, lines[1].Id, 100_000m, LedgerSide.Credit), (ledger[1].SupplierId!.Value, ledger[1].ContractId!.Value, ledger[1].AmountUsd, ledger[1].Side));

        var movements = await db.InventoryMovements.OrderBy(m => m.ContractId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal([100m, 200m], movements.Select(m => m.QuantityMt));
        Assert.Equal(300m, await new StockService(db).GetFreeQuantityMtAsync(ProductId, terminalId: 1, storageTankId: TankId));
    }

    [Fact]
    public async Task Payment_On_One_Line_Does_Not_Touch_The_Other_Supplier()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var lines = await db.Contracts.OrderBy(c => c.Id).ToListAsync();
        db.LedgerEntries.AddRange(
            NewLedger(1, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 60_000m, lines[0].Id, Akhund),
            NewLedger(2, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 100_000m, lines[1].Id, Mahmood),
            NewLedger(3, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 25_000m, lines[0].Id, Akhund));
        await db.SaveChangesAsync();

        var balances = await PartyBalanceReadService.CreateDefault(db)
            .GetContractBalancesAsync(lines.Select(l => l.Id).ToList());

        var akhund = Assert.Single(balances[lines[0].Id].Parties);
        Assert.Equal((PartyStatementPartyType.Supplier, Akhund), (akhund.PartyType, akhund.PartyId));
        Assert.Equal(-35_000m, akhund.ClosingBalanceUsd);
        var mahmood = Assert.Single(balances[lines[1].Id].Parties);
        Assert.Equal((PartyStatementPartyType.Supplier, Mahmood), (mahmood.PartyType, mahmood.PartyId));
        Assert.Equal(-100_000m, mahmood.ClosingBalanceUsd);
    }

    [Fact]
    public async Task Details_Show_Partial_Receipt_And_Balance_For_Each_Line_Separately()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var lines = await db.Contracts.OrderBy(c => c.Id).ToListAsync();
        var loading = new LoadingRegister
        {
            ContractId = lines[0].Id,
            ProductId = ProductId,
            TransportType = LoadingTransportType.Truck,
            LoadingDate = new DateTime(2026, 9, 2),
            LoadedQuantityMt = 40m,
            LoadingPriceUsd = 600m,
            SettlementCurrencyCode = "USD"
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        db.LoadingReceipts.AddRange(
            new LoadingReceipt
            {
                LoadingRegisterId = loading.Id,
                TerminalId = 1,
                StorageTankId = TankId,
                ReceiptDate = new DateTime(2026, 9, 3),
                ReceivedQuantityMt = 15m
            },
            new LoadingReceipt
            {
                LoadingRegisterId = loading.Id,
                TerminalId = 1,
                StorageTankId = TankId,
                ReceiptDate = new DateTime(2026, 9, 4),
                ReceivedQuantityMt = 99m,
                IsCancelled = true
            });
        db.LedgerEntries.AddRange(
            NewLedger(1, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 24_000m, lines[0].Id, Akhund),
            NewLedger(2, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 4_000m, lines[0].Id, Akhund));
        await db.SaveChangesAsync();
        var group = await db.PurchaseContractGroups.SingleAsync();

        var result = await NewController(db).PurchaseGroup(group.Id, PartyBalanceReadService.CreateDefault(db), default);

        var model = Assert.IsType<PurchaseContractGroupDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
        var akhund = model.Lines.Single(l => l.SupplierId == Akhund);
        Assert.Equal(60_000m, akhund.ValueUsd);
        Assert.Equal(40m, akhund.LoadedQuantityMt);
        Assert.Equal(15m, akhund.ReceivedQuantityMt);
        Assert.Equal(60m, akhund.RemainingQuantityMt);
        Assert.Equal(4_000m, akhund.PaidUsd);
        Assert.Equal(-20_000m, akhund.SupplierBalanceUsd);
        var mahmood = model.Lines.Single(l => l.SupplierId == Mahmood);
        Assert.Equal(0m, mahmood.LoadedQuantityMt);
        Assert.Equal(0m, mahmood.ReceivedQuantityMt);
        Assert.Equal(200m, mahmood.RemainingQuantityMt);
        Assert.Equal(0m, mahmood.SupplierBalanceUsd);
        Assert.Equal(160_000m, model.Totals.TotalValueUsd);
        Assert.Equal(533.3333m, model.Totals.WeightedAveragePriceUsd);
    }

    [Fact]
    public async Task Cancelled_Line_Stays_Visible_But_Leaves_The_Totals()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        (await db.Contracts.SingleAsync(c => c.SupplierId == Mahmood)).Status = ContractStatus.Cancelled;
        await db.SaveChangesAsync();
        var group = await db.PurchaseContractGroups.SingleAsync();

        var result = await NewController(db).PurchaseGroup(group.Id, PartyBalanceReadService.CreateDefault(db), default);

        var model = Assert.IsType<PurchaseContractGroupDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(2, model.Lines.Count);
        Assert.Equal(100m, model.Totals.TotalQuantityMt);
        Assert.Equal(1, model.Totals.SupplierCount);
    }

    [Fact]
    public async Task Added_Line_Inherits_Terms_From_The_Group()
    {
        await using var db = NewDb();
        Seed(db);
        db.Suppliers.Add(new Supplier { Id = 3, Name = "Third", IsActive = true });
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var group = await db.PurchaseContractGroups.SingleAsync();

        var result = await NewController(db).AddPurchaseGroupLine(group.Id, Line(3, 30m, 700m));

        Assert.IsType<RedirectToActionResult>(result);
        var added = await db.Contracts.SingleAsync(c => c.SupplierId == 3);
        Assert.Equal(group.Id, added.PurchaseContractGroupId);
        Assert.Equal(700m, added.UnitPriceUsd);
        Assert.Equal(ContractStatus.Active, added.Status);
        Assert.Equal(3, await db.Contracts.CountAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Line_With_Records_Cannot_Be_Deleted_And_Last_Line_Removes_The_Header()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var lines = await db.Contracts.OrderBy(c => c.Id).ToListAsync();
        db.LedgerEntries.Add(NewLedger(1, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 10m, lines[0].Id, Akhund));
        await db.SaveChangesAsync();

        await NewController(db).Delete(lines[0].Id);
        Assert.True(await db.Contracts.AnyAsync(c => c.Id == lines[0].Id));

        await NewController(db).Delete(lines[1].Id);
        Assert.False(await db.Contracts.AnyAsync(c => c.Id == lines[1].Id));
        Assert.Single(await db.PurchaseContractGroups.ToListAsync());

        db.LedgerEntries.RemoveRange(db.LedgerEntries);
        await db.SaveChangesAsync();
        await NewController(db).Delete(lines[0].Id);
        Assert.Empty(await db.PurchaseContractGroups.ToListAsync());
    }

    [Fact]
    public async Task Editing_A_Line_Keeps_It_In_The_Group()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var line = await db.Contracts.AsNoTracking().OrderBy(c => c.Id).FirstAsync();
        db.ChangeTracker.Clear();

        var result = await NewController(db).Edit(line.Id, new ContractFormViewModel
        {
            Id = line.Id,
            Version = line.Version,
            ContractName = line.ContractName,
            ContractNumber = line.ContractNumber,
            ContractType = ContractType.Purchase,
            Status = line.Status,
            CompanyId = line.CompanyId,
            ProductId = line.ProductId,
            UnitId = line.UnitId,
            SupplierId = line.SupplierId,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = line.ContractDate,
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 120m,
            Currency = "USD",
            UnitPriceInCurrency = 600m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == line.Id);
        Assert.Equal(120m, saved.QuantityMt);
        Assert.Equal(line.PurchaseContractGroupId, saved.PurchaseContractGroupId);
        Assert.NotNull(saved.PurchaseContractGroupId);
    }

    [Fact]
    public async Task Contract_List_Shows_One_Parent_Row_For_Three_Suppliers_With_Correct_Totals()
    {
        await using var db = NewDb();
        Seed(db);
        db.Suppliers.Add(new Supplier { Id = 3, Code = "S3", Name = "Karim", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 900,
            ContractNumber = "PUR-STANDALONE",
            ContractName = "تکی",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = ProductId,
            UnitId = 1,
            SupplierId = Akhund,
            ContractDate = new DateTime(2026, 8, 1),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 40m,
            Currency = "USD",
            UnitPriceInCurrency = 610m,
            UnitPriceUsd = 610m
        });
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m), Line(3, 150m, 550m)));

        var view = Assert.IsType<ViewResult>(await NewController(db).Index(null, null, null));
        var model = Assert.IsType<ContractIndexViewModel>(view.Model);

        Assert.Equal(2, model.TotalCount);
        Assert.Equal(2, model.PurchaseCount);
        Assert.Equal(2, model.ActiveCount);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(900, model.Rows[0].Contract!.Id);
        var group = model.Rows[1].Group!;
        Assert.Equal("P-001", group.GroupNumber);
        Assert.Equal(3, group.Lines.Count);
        Assert.Equal(450m, group.Totals.TotalQuantityMt);
        Assert.Equal(242_500m, group.Totals.TotalValueUsd);
        Assert.Equal(3, group.Totals.SupplierCount);
        Assert.Equal(538.8889m, group.Totals.WeightedAveragePriceUsd);
        Assert.Equal(ContractStatus.Active, group.Status);
        Assert.Equal(group.Totals.TotalQuantityMt, group.Lines.Sum(line => line.QuantityMt));
        Assert.Equal(4, model.Items.Count);

        // جست‌وجوی نام یک تأمین‌کننده باز هم قرارداد مادر را با همهٔ ردیف‌هایش نشان می‌دهد.
        var search = Assert.IsType<ContractIndexViewModel>(
            Assert.IsType<ViewResult>(await NewController(db).Index("Karim", null, null)).Model);
        var found = Assert.Single(search.Rows);
        Assert.Equal(3, found.Group!.Lines.Count);
        Assert.Equal(1, search.TotalCount);
    }

    [Fact]
    public async Task Contract_List_Excludes_Cancelled_Line_From_Totals_And_Counts_Parent_Once()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var cancelled = await db.Contracts.OrderBy(c => c.Id).LastAsync();
        cancelled.Status = ContractStatus.Cancelled;
        await db.SaveChangesAsync();

        var model = Assert.IsType<ContractIndexViewModel>(
            Assert.IsType<ViewResult>(await NewController(db).Index(null, null, null)).Model);

        var group = Assert.Single(model.Rows).Group!;
        Assert.Equal(2, group.Lines.Count);
        Assert.Equal(100m, group.Totals.TotalQuantityMt);
        Assert.Equal(60_000m, group.Totals.TotalValueUsd);
        Assert.Equal(1, model.TotalCount);
        Assert.Equal(1, model.ActiveCount);

        var cancelledOnly = Assert.IsType<ContractIndexViewModel>(
            Assert.IsType<ViewResult>(await NewController(db).Index(null, null, [ContractStatus.Cancelled])).Model);
        Assert.Equal(1, cancelledOnly.TotalCount);
        Assert.Equal(0, cancelledOnly.ActiveCount);
    }

    [Fact]
    public void Parent_Status_Is_Derived_From_Lines()
    {
        Assert.Equal(ContractStatus.Active, PurchaseGroupStatus.Derive([ContractStatus.Cancelled, ContractStatus.Active]));
        Assert.Equal(ContractStatus.Draft, PurchaseGroupStatus.Derive([ContractStatus.Closed, ContractStatus.Draft]));
        Assert.Equal(ContractStatus.Closed, PurchaseGroupStatus.Derive([ContractStatus.Closed, ContractStatus.Cancelled]));
        Assert.Equal(ContractStatus.Cancelled, PurchaseGroupStatus.Derive([ContractStatus.Cancelled, ContractStatus.Cancelled]));
    }

    [Fact]
    public void List_Grouping_Makes_One_Unit_Per_Parent_In_Original_Order()
    {
        var units = ContractListGrouping.Build(
        [
            new ContractListKey(1, null, ContractType.Sale, ContractStatus.Active),
            new ContractListKey(2, 7, ContractType.Purchase, ContractStatus.Closed),
            new ContractListKey(3, 7, ContractType.Purchase, ContractStatus.Active),
            new ContractListKey(4, null, ContractType.Purchase, ContractStatus.Draft),
            new ContractListKey(5, 7, ContractType.Purchase, ContractStatus.Cancelled)
        ]);

        Assert.Equal(3, units.Count);
        Assert.Equal((int?)1, units[0].ContractId);
        Assert.Equal((int?)7, units[1].GroupId);
        Assert.Null(units[1].ContractId);
        Assert.Equal(ContractStatus.Active, units[1].Status);
        Assert.Equal((int?)4, units[2].ContractId);
    }

    [Fact]
    public async Task Selector_Lookup_Maps_Every_Line_To_Its_Parent_And_Offers_Parent_First()
    {
        await using var db = NewDb();
        Seed(db);
        db.Contracts.Add(new Contract
        {
            Id = 900,
            ContractNumber = "PUR-STANDALONE",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = Akhund,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 40m
        });
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var lineNumbers = await db.Contracts
            .Where(c => c.PurchaseContractGroupId != null)
            .OrderBy(c => c.Id)
            .Select(c => c.ContractNumber)
            .ToListAsync();

        var lookup = await PurchaseContractGroupLookup.LoadByContractNumberAsync(
            db, lineNumbers.Append("PUR-STANDALONE").Append(""));

        Assert.Equal(2, lookup.Count);
        Assert.All(lineNumbers, number => Assert.Equal("P-001", lookup[number].GroupNumber));
        Assert.False(lookup.ContainsKey("PUR-STANDALONE"));

        // دو منبع از ردیف اول و یک منبع از ردیف دوم (مثلاً دو بارگیری و یک موجودی مخزن).
        var options = PurchaseContractGroupLookup.BuildContractFilterOptions(
        [
            (lineNumbers[0], lookup[lineNumbers[0]]),
            (lineNumbers[0], lookup[lineNumbers[0]]),
            (lineNumbers[1], lookup[lineNumbers[1]]),
            ("PUR-STANDALONE", null)
        ]);

        Assert.Equal(4, options.Count);
        Assert.Equal("group:P-001", options[0].Value);
        Assert.Contains("2 ردیف", options[0].Label);
        Assert.Contains(options, option => option.Value == lineNumbers[0] && option.Label.Contains("P-001"));
        Assert.Contains(options, option => option.Value == "PUR-STANDALONE" && option.Label == "PUR-STANDALONE");
    }

    [Fact]
    public void Shared_Expense_For_The_Parent_Is_Split_By_Quantity_And_Sums_Exactly()
    {
        // ۹۰۰ دالر کرایه برای ۱۰۰ + ۲۰۰ + ۱۵۰ تن.
        Assert.Equal(new[] { 200m, 400m, 300m },
            PTGOilSystem.Web.Services.Expenses.ExpenseAmountAllocator.SplitByWeight(900m, [100m, 200m, 150m]));

        // مبلغ غیرقابل‌تقسیم: سنت باقی‌مانده به بزرگ‌ترین کسر می‌رسد، نه همیشه ردیف آخر.
        var shares = PTGOilSystem.Web.Services.Expenses.ExpenseAmountAllocator.SplitByWeight(1000m, [100m, 200m, 150m]);
        Assert.Equal(new[] { 222.22m, 444.45m, 333.33m }, shares);
        Assert.Equal(1000m, shares.Sum());

        var many = PTGOilSystem.Web.Services.Expenses.ExpenseAmountAllocator.SplitByWeight(
            100m, Enumerable.Repeat(1m, 3).ToList());
        Assert.Equal(100m, many.Sum());
        Assert.All(many, share => Assert.True(share is 33.33m or 33.34m));
    }

    [Fact]
    public async Task Parent_Lifecycle_Is_The_Sum_Of_Each_Line_Standard_Contract_Summary()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();
        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));
        var lineIds = await db.Contracts.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
        var journey = new ContractJourneyController(db, new StockService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var lines = await journey.BuildPurchaseGroupLifecycleAsync(lineIds);
        var total = PTGOilSystem.Web.Models.ContractJourney.ContractLifecycleFigures.Sum(lines.Select(l => l.Figures));

        Assert.Equal(2, lines.Count);
        Assert.Equal(100m, lines[0].Figures.ContractQuantityMt);
        Assert.Equal(60_000m, lines[0].Figures.ContractValueUsd);
        Assert.Equal(100_000m, lines[1].Figures.ContractValueUsd);
        Assert.Equal(300m, total.ContractQuantityMt);
        Assert.Equal(160_000m, total.ContractValueUsd);
        Assert.Equal(300m, total.RemainingToLoadMt);
        Assert.Equal(lines.Sum(l => l.Figures.ExpenseTotalUsd), total.ExpenseTotalUsd);
        Assert.Equal(lines.Sum(l => l.Figures.LifecycleProfitUsd), total.LifecycleProfitUsd);
    }

    [Fact]
    public async Task Parent_Takes_The_Next_Purchase_Number_And_Lines_Use_Its_Suffix()
    {
        await using var db = NewDb();
        Seed(db);
        db.Contracts.Add(new Contract
        {
            Id = 900,
            ContractNumber = "P-005",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            SupplierId = Akhund,
            ContractDate = new DateTime(2026, 8, 1),
            QuantityMt = 40m
        });
        await db.SaveChangesAsync();

        await NewController(db).Create(NewMultiModel(Line(Akhund, 100m, 600m), Line(Mahmood, 200m, 500m)));

        var group = await db.PurchaseContractGroups.Include(g => g.Lines).SingleAsync();
        Assert.Equal("P-006", group.GroupNumber);
        Assert.Equal(new[] { "P-006-1", "P-006-2" }, group.Lines.OrderBy(c => c.Id).Select(c => c.ContractNumber));

        db.Suppliers.Add(new Supplier { Id = 3, Code = "S3", Name = "Karim", IsActive = true });
        await db.SaveChangesAsync();
        await NewController(db).AddPurchaseGroupLine(group.Id, Line(3, 30m, 700m));
        Assert.True(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-006-3" && c.PurchaseContractGroupId == group.Id));

        // قرارداد عادی بعدی شمارهٔ مادر را تکرار نمی‌کند.
        await NewController(db).Create(new ContractFormViewModel
        {
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = ProductId,
            UnitId = 1,
            SupplierId = Akhund,
            OwnershipType = ContractOwnershipType.Personal,
            ContractDate = new DateTime(2026, 9, 1),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 40m,
            Currency = "USD",
            UnitPriceInCurrency = 600m
        });
        Assert.True(await db.Contracts.AnyAsync(c => c.ContractNumber == "P-007" && c.PurchaseContractGroupId == null));
    }

    private static PurchaseContractLineInput Line(int supplierId, decimal quantity, decimal price) => new()
    {
        SupplierId = supplierId,
        ProductId = ProductId,
        QuantityMt = quantity,
        UnitPrice = price
    };

    private static ContractFormViewModel NewMultiModel(params PurchaseContractLineInput[] lines) => new()
    {
        IsMultiSupplier = true,
        ContractName = "دیزل نوامبر",
        ContractType = ContractType.Purchase,
        Status = ContractStatus.Active,
        CompanyId = 1,
        OwnershipType = ContractOwnershipType.Personal,
        ContractDate = new DateTime(2026, 9, 1),
        Currency = "USD",
        RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
        PurchaseLines = lines.ToList()
    };

    private static LedgerEntry NewLedger(int id, string sourceType, LedgerSide side, decimal amount, int contractId, int supplierId) => new()
    {
        Id = id,
        EntryDate = new DateTime(2026, 9, id + 1),
        Side = side,
        AmountUsd = amount,
        SourceType = sourceType,
        SourceId = id,
        ContractId = contractId,
        SupplierId = supplierId,
        Description = sourceType
    };

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", IsActive = true });
        db.Products.Add(new Product { Id = ProductId, Code = "GO", Name = "Gas Oil", IsActive = true, UnitId = 1 });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Suppliers.AddRange(
            new Supplier { Id = Akhund, Code = "S1", Name = "Akhund", IsActive = true },
            new Supplier { Id = Mahmood, Code = "S2", Name = "Mahmood", IsActive = true });
        db.Locations.Add(new Location { Id = 7, Name = "Hairatan", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "TERM-1", Name = "Terminal", IsActive = true });
        db.StorageTanks.Add(new StorageTank
        {
            Id = TankId,
            TerminalId = 1,
            TankCode = "TK-01",
            ProductId = ProductId,
            CapacityMt = 8000m,
            IsActive = true
        });
    }

    private static SimplePurchaseWorkflowService NewWorkflow(ApplicationDbContext db)
    {
        var audit = new AuditService(db);
        var stock = new StockService(db);
        var movements = new InventoryMovementWriter(db, stock);
        var accounting = new SkippedPurchaseAccounting();
        var cancellation = new LoadingReceiptCancellationService(
            db,
            audit,
            NullLogger<LoadingReceiptCancellationService>.Instance,
            stock,
            accounting,
            movements: movements);
        return new SimplePurchaseWorkflowService(db, movements, accounting, cancellation, audit, new LedgerPostingService(db));
    }

    private static ContractsController NewController(ApplicationDbContext db, bool simplePurchaseEnabled = false)
        => new(
            db,
            new AuditService(db),
            new CurrencyConversionService(new PricingService(db)),
            new FormTokenGuard(db),
            clientProfileOptions: Options.Create(new ClientProfileOptions { SimplePurchaseEnabled = simplePurchaseEnabled }))
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

    private sealed class SkippedPurchaseAccounting : IPurchaseAccountingAdapter
    {
        private static readonly PurchaseAccountingResult Skipped = new(PaymentPostingStatus.Skipped, null, "test");

        public Task<PurchaseAccountingResult> TryPostPurchaseAsync(LoadingRegister loading, CancellationToken cancellationToken = default)
            => Task.FromResult(Skipped);

        public Task<IReadOnlyList<PurchaseAccountingResult>> TryPostPurchasesAsync(IReadOnlyList<LoadingRegister> loadings, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PurchaseAccountingResult> TryPostInventoryReceiptAsync(LoadingReceipt receipt, CancellationToken cancellationToken = default)
            => Task.FromResult(Skipped);

        public Task<PurchaseAccountingResult> TryPostTransportReceiptAsync(InventoryTransportReceipt receipt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PurchaseAccountingResult> TryPostPurchaseReversalAsync(LoadingRegister loading, CancellationToken cancellationToken = default)
            => Task.FromResult(Skipped);

        public Task<PurchaseAccountingResult> TryPostInventoryReceiptReversalAsync(LoadingReceipt receipt, CancellationToken cancellationToken = default)
            => Task.FromResult(Skipped);

        public Task<PurchaseAccountingResult> TryPostTransportReceiptReversalAsync(InventoryTransportReceipt receipt, CancellationToken cancellationToken = default)
            => Task.FromResult(Skipped);
    }
}
