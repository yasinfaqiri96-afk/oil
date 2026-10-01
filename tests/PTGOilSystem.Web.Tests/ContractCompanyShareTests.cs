using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «سهم شرکت %» در فرم قرارداد شراکتی فقط نمای سادهٔ سهمِ مالک دفترِ شرکت است
/// (Company.OwnerPartnerId)؛ ذخیره همان سطرهای ContractPartner قبلی را می‌سازد و
/// صورت‌حساب شریک عدد به عدد همان می‌ماند.
/// </summary>
public class ContractCompanyShareTests
{
    private const int CompanyA = 1;       // مالک دفتر: فواد
    private const int CompanyB = 2;       // مالک دفتر: فیض‌الله
    private const int CompanyNoOwner = 3; // بدون مالک دفتر
    private const int Fawad = 1;
    private const int Faizullah = 2;
    private const int Ahmad = 3;
    private const int Yusuf = 4;

    // ————————————————— ایجاد —————————————————

    [Fact]
    public async Task CompanyA_35Percent_WithTwoPartners_SavesTheBookOwnerRow()
    {
        await using var db = NewDb();
        Seed(db);

        var result = await BuildContracts(db).Create(NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(
            new[] { (Fawad, 35m), (Ahmad, 40m), (Yusuf, 25m) },
            await SharesAsync(db));
    }

    [Fact]
    public async Task CompanyB_60Percent_WithOnePartner_SavesItsOwnBookOwnerRow()
    {
        await using var db = NewDb();
        Seed(db);

        var result = await BuildContracts(db).Create(NewModel(CompanyB, 60m, (Ahmad, 40m)));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(new[] { (Faizullah, 60m), (Ahmad, 40m) }, await SharesAsync(db));
    }

    [Fact]
    public async Task CompanyShareZero_SavesOnlyTheOtherPartners()
    {
        await using var db = NewDb();
        Seed(db);

        var result = await BuildContracts(db).Create(NewModel(CompanyA, 0m, (Ahmad, 60m), (Yusuf, 40m)));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(new[] { (Ahmad, 60m), (Yusuf, 40m) }, await SharesAsync(db));
    }

    [Theory]
    [InlineData(35, 40, 20)]
    [InlineData(35, 40, 30)]
    public async Task TotalOtherThan100_IsRejected(int company, int ahmad, int yusuf)
    {
        await using var db = NewDb();
        Seed(db);
        var controller = BuildContracts(db);

        var result = await controller.Create(NewModel(CompanyA, company, (Ahmad, ahmad), (Yusuf, yusuf)));

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.PartnerShares)));
        Assert.Empty(db.ContractPartners);
    }

    [Fact]
    public async Task BookOwnerPickedAgainAmongOtherPartners_IsRejected()
    {
        await using var db = NewDb();
        Seed(db);
        var controller = BuildContracts(db);

        var result = await controller.Create(NewModel(CompanyA, 35m, (Fawad, 40m), (Yusuf, 25m)));

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.PartnerShares)));
        Assert.Empty(db.ContractPartners);
    }

    [Fact]
    public async Task CompanyShare_ForACompanyWithoutBookOwner_IsRejected()
    {
        await using var db = NewDb();
        Seed(db);
        var controller = BuildContracts(db);

        var result = await controller.Create(NewModel(CompanyNoOwner, 30m, (Ahmad, 70m)));

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.CompanySharePercent)));
        Assert.Empty(db.ContractPartners);
    }

    [Fact]
    public async Task CompanyShare_Above100_IsRejected()
    {
        await using var db = NewDb();
        Seed(db);
        var controller = BuildContracts(db);

        var result = await controller.Create(NewModel(CompanyA, 120m));

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ContractFormViewModel.CompanySharePercent)));
    }

    [Fact]
    public async Task Personal_IgnoresTheCompanyShareField()
    {
        await using var db = NewDb();
        Seed(db);
        var model = NewModel(CompanyA, 35m, (Ahmad, 65m));
        model.OwnershipType = ContractOwnershipType.Personal;

        var result = await BuildContracts(db).Create(model);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(db.ContractPartners);
    }

    // ————————————————— ویرایش —————————————————

    [Fact]
    public async Task EditForm_ShowsTheBookOwnerShareAsCompanyShare_AndNotAmongOtherPartners()
    {
        await using var db = NewDb();
        Seed(db);
        var contractId = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));

        var view = Assert.IsType<ViewResult>(await BuildContracts(db).Edit(contractId));
        var model = Assert.IsType<ContractFormViewModel>(view.Model);

        Assert.Equal(35m, model.CompanySharePercent);
        Assert.DoesNotContain(model.PartnerShares, p => p.PartnerId == Fawad);
        Assert.Equal(new[] { Ahmad, Yusuf }, model.PartnerShares.Select(p => p.PartnerId!.Value).OrderBy(id => id));
    }

    [Fact]
    public async Task EditSavedWithoutChanges_KeepsTheSameShareRows()
    {
        await using var db = NewDb();
        Seed(db);
        var contractId = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        var before = await SliceRowsAsync(db);

        var result = await SaveEditFormUnchangedAsync(db, contractId);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(before, await SliceRowsAsync(db));
    }

    [Fact]
    public async Task EditChangingCompany_MovesCompanyShareToTheNewBookOwner_AndKeepsHistory()
    {
        await using var db = NewDb();
        Seed(db);
        var contractId = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        var contract = await db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contractId);

        var model = NewModel(CompanyB, 35m, (Ahmad, 40m), (Yusuf, 25m));
        model.Id = contractId;
        model.ContractNumber = contract.ContractNumber;
        model.Version = contract.Version;
        var result = await BuildContracts(db).Edit(contractId, model);

        Assert.IsType<RedirectToActionResult>(result);
        var rows = await db.ContractPartners.AsNoTracking().Where(cp => cp.ContractId == contractId).ToListAsync();
        var latestStart = rows.Max(r => r.EffectiveFrom);
        Assert.Equal(
            new[] { (Faizullah, 35m), (Ahmad, 40m), (Yusuf, 25m) },
            rows.Where(r => r.EffectiveFrom == latestStart)
                .OrderBy(r => r.PartnerId)
                .Select(r => (r.PartnerId, r.SharePercent)));
        // بازهٔ قبلی (فواد ۳۵٪) پاک نشده و فقط بسته شده است.
        var closedFawad = Assert.Single(rows, r => r.PartnerId == Fawad);
        Assert.Equal(latestStart, closedFawad.EffectiveTo);

        // فرم ویرایش حالا مالک دفترِ شرکت تازه را به‌عنوان «سهم شرکت» می‌شناسد.
        var view = Assert.IsType<ViewResult>(await BuildContracts(db).Edit(contractId));
        var reopened = Assert.IsType<ContractFormViewModel>(view.Model);
        Assert.Equal(35m, reopened.CompanySharePercent);
        Assert.DoesNotContain(reopened.PartnerShares, p => p.PartnerId == Faizullah);
    }

    // ————————————————— صورت‌حساب: قبل و بعد یکسان —————————————————

    [Fact]
    public async Task Statement_IsIdentical_WhetherOwnerIsEnteredAsPartnerOrAsCompanyShare()
    {
        await using var legacyDb = NewDb();
        Seed(legacyDb);
        var legacy = NewModel(CompanyA, companyShare: null, (Fawad, 35m), (Ahmad, 40m), (Yusuf, 25m));
        var legacyContract = await CreateAsync(legacyDb, legacy);
        await AddActivityAsync(legacyDb, legacyContract);

        await using var newDb = NewDb();
        Seed(newDb);
        var newContract = await CreateAsync(newDb, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        await AddActivityAsync(newDb, newContract);

        Assert.Equal(await SliceRowsAsync(legacyDb), await SliceRowsAsync(newDb));
        foreach (var partnerId in new[] { Fawad, Ahmad, Yusuf })
        {
            AssertSameStatement(
                await new PartnershipStatementService(legacyDb).BuildForPartnerAsync(partnerId),
                await new PartnershipStatementService(newDb).BuildForPartnerAsync(partnerId));
        }
    }

    [Fact]
    public async Task Statement_IsIdentical_BeforeAndAfterSavingTheNewEditForm()
    {
        await using var db = NewDb();
        Seed(db);
        var contractId = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        await AddActivityAsync(db, contractId);
        var before = new Dictionary<int, PartnerAccountStatement?>();
        foreach (var partnerId in new[] { Fawad, Ahmad, Yusuf })
        {
            before[partnerId] = await new PartnershipStatementService(db).BuildForPartnerAsync(partnerId);
        }

        Assert.IsType<RedirectToActionResult>(await SaveEditFormUnchangedAsync(db, contractId));

        foreach (var partnerId in new[] { Fawad, Ahmad, Yusuf })
        {
            AssertSameStatement(before[partnerId], await new PartnershipStatementService(db).BuildForPartnerAsync(partnerId));
        }
    }

    // ————————————————— صفحهٔ شرکت و فهرست شرکا —————————————————

    [Fact]
    public async Task CompanyPage_PartnershipStatus_IsTheBookOwnersPartnerStatement()
    {
        await using var db = NewDb();
        Seed(db);
        var contractId = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        await AddActivityAsync(db, contractId);

        var controller = new CompaniesController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        Assert.IsType<ViewResult>(await controller.Details(CompanyA));

        var shown = Assert.IsType<PartnerAccountStatement>(controller.ViewData["OwnerPartnerStatement"]);
        AssertSameStatement(await new PartnershipStatementService(db).BuildForPartnerAsync(Fawad), shown);
        Assert.Equal(35m, Assert.Single(shown.Contracts).SharePercent);
        // عدد واقعی دارد، نه صفرِ دوطرفه: پرداخت فواد و ۳۵٪ از مفاد ۷٬۰۰۰ دالری.
        Assert.Equal(30_000m, shown.FundingUsd);
        Assert.Equal(2_450m, shown.ProfitShareUsd);
    }

    [Fact]
    public async Task CompanyPage_WithoutBookOwner_HasNoPartnershipStatement()
    {
        await using var db = NewDb();
        Seed(db);

        var controller = new CompaniesController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        Assert.IsType<ViewResult>(await controller.Details(CompanyNoOwner));

        Assert.Null(controller.ViewData["OwnerPartnerStatement"]);
    }

    [Fact]
    public async Task CompanyPage_ShowsOnlyThisCompanysContracts_NotTheOwnersExternalOnes()
    {
        await using var db = NewDb();
        Seed(db);
        // قرارداد شرکت A: فواد مالک دفتر با ۳۵٪.
        var contractA = await CreateAsync(db, NewModel(CompanyA, 35m, (Ahmad, 40m), (Yusuf, 25m)));
        await AddActivityAsync(db, contractA);
        // قرارداد شرکت B: فیض‌الله مالک دفتر با ۶۰٪، فواد شریک بیرونی با ۴۰٪.
        var contractB = await CreateAsync(db, NewModel(CompanyB, 60m, (Fawad, 40m)));
        await AddActivityAsync(db, contractB, (Faizullah, 25_000m), (Fawad, 20_000m));

        var companyA = await CompanyStatusAsync(db, CompanyA);
        var companyB = await CompanyStatusAsync(db, CompanyB);
        var fawadFull = await new PartnershipStatementService(db).BuildForPartnerAsync(Fawad);

        // صفحهٔ A فقط قرارداد A و سهم ۳۵٪ فواد؛ پرداخت ۲۰٬۰۰۰ فواد در قرارداد B دیده نمی‌شود.
        var shownA = Assert.Single(companyA!.Contracts);
        Assert.Equal((contractA, 35m), (shownA.ContractId, shownA.SharePercent));
        Assert.All(companyA.Entries, e => Assert.Equal(contractA, e.ContractId));
        Assert.Equal(30_000m, companyA.FundingUsd);
        AssertSameStatement(await new PartnershipStatementService(db).BuildForPartnerAsync(Fawad, [contractA]), companyA);

        // صفحهٔ B فقط قرارداد B و سهم ۶۰٪ فیض‌الله.
        var shownB = Assert.Single(companyB!.Contracts);
        Assert.Equal((contractB, 60m), (shownB.ContractId, shownB.SharePercent));
        Assert.Equal(Faizullah, companyB.PartnerId);

        // صورت‌حساب کامل فواد دست‌نخورده: هر دو قرارداد و هر دو پرداخت.
        Assert.Equal(new[] { contractA, contractB }, fawadFull!.Contracts.Select(c => c.ContractId).OrderBy(id => id));
        Assert.Equal(50_000m, fawadFull.FundingUsd);
    }

    [Fact]
    public async Task CompanyPage_OwnerOnlyExternalElsewhere_ShowsNothingInsteadOfOtherCompanies()
    {
        await using var db = NewDb();
        Seed(db);
        // فواد فقط در قرارداد شرکت B شریک بیرونی است؛ شرکت A هیچ قرارداد شراکتی ندارد.
        var contractB = await CreateAsync(db, NewModel(CompanyB, 60m, (Fawad, 40m)));
        await AddActivityAsync(db, contractB, (Faizullah, 25_000m), (Fawad, 20_000m));

        Assert.Null(await CompanyStatusAsync(db, CompanyA));
    }

    [Fact]
    public async Task Picker_OwnerOfAnotherCompany_CanStillBeAnExternalPartner()
    {
        await using var db = NewDb();
        Seed(db);
        var controller = BuildContracts(db);

        // فواد مالک دفتر شرکت A است، ولی در قرارداد شرکت B شریک بیرونی می‌شود.
        var result = await controller.Create(NewModel(CompanyB, 60m, (Fawad, 40m)));

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);
        Assert.Equal(new[] { (Fawad, 40m), (Faizullah, 60m) },
            (await db.ContractPartners.AsNoTracking().OrderBy(cp => cp.PartnerId).ToListAsync())
                .Select(cp => (cp.PartnerId, cp.SharePercent)));
    }

    [Fact]
    public async Task PartnersList_DoesNotRepeatBookOwnersAsOrdinaryPartners()
    {
        await using var db = NewDb();
        Seed(db);

        var controller = new PartnersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        var view = Assert.IsType<ViewResult>(await controller.Index(q: null));
        var partners = Assert.IsAssignableFrom<IEnumerable<Partner>>(view.Model);

        Assert.Equal(new[] { Ahmad, Yusuf }, partners.Select(p => p.Id).OrderBy(id => id));
    }

    // ————————————————— کمک‌کننده‌ها —————————————————

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", IsActive = true });
        db.Partners.AddRange(
            new Partner { Id = Fawad, Code = "P1", Name = "Fawad", IsActive = true },
            new Partner { Id = Faizullah, Code = "P2", Name = "Faizullah", IsActive = true },
            new Partner { Id = Ahmad, Code = "P3", Name = "Ahmad", IsActive = true },
            new Partner { Id = Yusuf, Code = "P4", Name = "Yusuf", IsActive = true });
        db.Companies.AddRange(
            new Company { Id = CompanyA, Code = "FW", Name = "Fawad Co", IsActive = true, OwnerPartnerId = Fawad },
            new Company { Id = CompanyB, Code = "FZ", Name = "Faizullah Co", IsActive = true, OwnerPartnerId = Faizullah },
            // IsSystemOwner هیچ نقشی در «سهم شرکت» ندارد.
            new Company { Id = CompanyNoOwner, Code = "NO", Name = "No Owner Co", IsActive = true, IsSystemOwner = true });
        db.SaveChanges();
    }

    private static async Task<PartnerAccountStatement?> CompanyStatusAsync(ApplicationDbContext db, int companyId)
    {
        var controller = new CompaniesController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        Assert.IsType<ViewResult>(await controller.Details(companyId));
        return controller.ViewData["OwnerPartnerStatement"] as PartnerAccountStatement;
    }

    private static ContractsController BuildContracts(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private static ContractFormViewModel NewModel(
        int companyId,
        decimal? companyShare,
        params (int PartnerId, decimal Share)[] others)
        => new()
        {
            ContractName = "قرارداد شراکتی آزمایشی",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = companyId,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            OwnershipType = ContractOwnershipType.Partnership,
            ContractDate = new DateTime(2026, 4, 23),
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = 100m,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
            CompanySharePercent = companyShare,
            PartnerShares = others
                .Select(o => new ContractPartnerShareInput { PartnerId = o.PartnerId, SharePercent = o.Share })
                .ToList()
        };

    private static async Task<int> CreateAsync(ApplicationDbContext db, ContractFormViewModel model)
    {
        Assert.IsType<RedirectToActionResult>(await BuildContracts(db).Create(model));
        return await db.Contracts.AsNoTracking().MaxAsync(c => c.Id);
    }

    /// <summary>کاربر فرم ویرایش را باز می‌کند و بدون تغییر ذخیره می‌کند.</summary>
    private static async Task<IActionResult> SaveEditFormUnchangedAsync(ApplicationDbContext db, int contractId)
    {
        var view = Assert.IsType<ViewResult>(await BuildContracts(db).Edit(contractId));
        var model = Assert.IsType<ContractFormViewModel>(view.Model);
        db.ChangeTracker.Clear();
        return await BuildContracts(db).Edit(contractId, model);
    }

    private static async Task<IEnumerable<(int, decimal)>> SharesAsync(ApplicationDbContext db)
        => (await db.ContractPartners.AsNoTracking().ToListAsync())
            .OrderBy(cp => cp.PartnerId == Fawad || cp.PartnerId == Faizullah ? 0 : 1)
            .ThenBy(cp => cp.PartnerId)
            .Select(cp => (cp.PartnerId, cp.SharePercent))
            .ToList();

    private static async Task<List<(int, decimal, DateTime, DateTime?)>> SliceRowsAsync(ApplicationDbContext db)
        => (await db.ContractPartners.AsNoTracking().ToListAsync())
            .OrderBy(cp => cp.PartnerId)
            .ThenBy(cp => cp.EffectiveFrom)
            .Select(cp => (cp.PartnerId, cp.SharePercent, cp.EffectiveFrom, cp.EffectiveTo))
            .ToList();

    /// <summary>بارگیری، فروش و پرداخت‌های شرکا — تا صورت‌حساب واقعاً عدد داشته باشد.</summary>
    private static Task AddActivityAsync(ApplicationDbContext db, int contractId)
        => AddActivityAsync(db, contractId, (Fawad, 30_000m), (Ahmad, 15_000m), (Yusuf, 2_500m));

    private static async Task AddActivityAsync(
        ApplicationDbContext db,
        int contractId,
        params (int PartnerId, decimal AmountUsd)[] partnerPayments)
    {
        var companyId = await db.Contracts.AsNoTracking()
            .Where(c => c.Id == contractId)
            .Select(c => c.CompanyId)
            .SingleAsync();
        db.LoadingRegisters.Add(new LoadingRegister
        {
            ContractId = contractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 450m
        });
        var sale = new SalesTransaction
        {
            CompanyId = companyId,
            ProductId = 1,
            InvoiceNumber = $"S-{contractId}",
            SaleDate = new DateTime(2026, 6, 1),
            QuantityMt = 100m,
            UnitPriceUsd = 520m,
            TotalUsd = 52_000m,
            Currency = "USD",
            TotalInCurrency = 52_000m,
            AppliedFxRateToUsd = 1m
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = sale.SaleDate,
            Side = LedgerSide.Credit,
            AmountUsd = sale.TotalUsd,
            Currency = "USD",
            ContractId = contractId,
            SourceType = "Sale",
            SourceId = sale.Id,
            Description = "Sale"
        });
        await db.SaveChangesAsync();

        var day = 2;
        foreach (var (partnerId, amountUsd) in partnerPayments)
        {
            await AddPartnerPaymentAsync(db, contractId, partnerId, amountUsd, PaymentKind.SupplierPayment, new DateTime(2026, 5, day++));
        }
    }

    private static async Task AddPartnerPaymentAsync(
        ApplicationDbContext db,
        int contractId,
        int partnerId,
        decimal amountUsd,
        PaymentKind kind,
        DateTime date)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = date,
            Direction = PaymentDirection.Out,
            PaymentKind = kind,
            ContractId = contractId,
            SupplierId = kind == PaymentKind.SupplierPayment ? 1 : null,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = kind.ToString(),
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = partnerId
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        var ledger = new LedgerEntry
        {
            EntryDate = date,
            Side = LedgerSide.Debit,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            SupplierId = payment.SupplierId,
            SourceType = kind.ToString(),
            SourceId = payment.Id,
            Description = payment.Description
        };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        payment.LedgerEntryId = ledger.Id;
        await db.SaveChangesAsync();
    }

    private static void AssertSameStatement(PartnerAccountStatement? expected, PartnerAccountStatement? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.FundingUsd, actual!.FundingUsd);
        Assert.Equal(expected.ProceedsHeldUsd, actual.ProceedsHeldUsd);
        Assert.Equal(expected.ProfitShareUsd, actual.ProfitShareUsd);
        Assert.Equal(expected.UnsoldCostShareUsd, actual.UnsoldCostShareUsd);
        Assert.Equal(expected.SettlementsPaidUsd, actual.SettlementsPaidUsd);
        Assert.Equal(expected.SettlementsReceivedUsd, actual.SettlementsReceivedUsd);
        Assert.Equal(expected.NetPositionUsd, actual.NetPositionUsd);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.AmountUsd, actual.AmountUsd);
        Assert.Equal(expected.TotalDebitUsd, actual.TotalDebitUsd);
        Assert.Equal(expected.TotalCreditUsd, actual.TotalCreditUsd);
        Assert.Equal(expected.Entries, actual.Entries);
        Assert.Equal(
            expected.Contracts.Select(c => (c.ContractId, c.SharePercent, c.FundingUsd, c.ProfitShareUsd, c.NetPositionUsd)),
            actual.Contracts.Select(c => (c.ContractId, c.SharePercent, c.FundingUsd, c.ProfitShareUsd, c.NetPositionUsd)));
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
