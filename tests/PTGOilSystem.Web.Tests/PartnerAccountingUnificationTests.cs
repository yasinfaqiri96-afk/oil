using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «یک تراکنش، یک معنی حسابداری، یک مانده» — اثباتِ اینکه ماندهٔ شریک در پروفایل شریک،
/// صورت‌حساب طرف‌حساب و گزارش «طلبات و بدهی‌ها» یک عدد و یک علامت می‌دهد، و اینکه
/// پرداختِ شرکتی که مالکش یک شریکِ همان قرارداد است، سرمایه‌گذاری همان شریک شمرده می‌شود.
///
/// سناریوی مرجع — قرارداد ۵۰۰ تن، شراکت ۵۰/۵۰:
///   فواد: خرید ۲۷۸٬۰۰۰ + مصارف ترکمنستان ۱۸٬۵۷۵ = ۲۹۶٬۵۷۵
///   یوسف: کرایه ۲۵٬۰۰۰ + شب‌خواب ۲٬۰۰۰ + گمرک ۲۷٬۱۵۵ = ۵۴٬۱۵۵
///   فروش ۴۴۷٬۹۱۱ (تمامش نزد یوسف مانده) — هزینه ۳۵۰٬۷۳۰ — مفاد ۹۷٬۱۸۱
///   سهم هرکس ۴۸٬۵۹۰٫۵۰
///   نتیجه: فواد +۳۴۵٬۱۶۵٫۵۰ (طلبکار) و یوسف −۳۴۵٬۱۶۵٫۵۰ (بدهکار)، جمع صفر.
/// </summary>
public sealed class PartnerAccountingUnificationTests
{
    private const decimal PurchaseUsd = 278_000m;
    private const decimal TurkmenExpenseUsd = 18_575m;
    private const decimal FreightUsd = 25_000m;
    private const decimal TruckWaitingUsd = 2_000m;
    private const decimal CustomsUsd = 27_155m;
    private const decimal ContractExpenseUsd = 72_730m;
    private const decimal SalesUsd = 447_911m;

    private const decimal FawadContribution = 296_575m;
    private const decimal YusufContribution = 54_155m;
    private const decimal TotalCostUsd = 350_730m;
    private const decimal ProfitUsd = 97_181m;
    private const decimal ProfitShareEach = 48_590.50m;
    private const decimal FawadNetPosition = 345_165.50m;

    // ————————————————— سناریوی پذیرش: یک عدد در همهٔ صفحات —————————————————

    [Fact]
    public async Task AcceptanceScenario_ContractBook_MatchesTheAgreedNumbers()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var statement = await new PartnershipStatementService(db).BuildAsync(s.FawadId, s.YusufId);
        Assert.NotNull(statement);
        var contract = statement!.Contracts.Single();

        Assert.Equal(SalesUsd, contract.SalesUsd);
        Assert.Equal(PurchaseUsd, contract.PurchaseCostUsd);
        Assert.Equal(ContractExpenseUsd, contract.OperationalExpenseUsd);
        Assert.Equal(TotalCostUsd, contract.TotalCostUsd);
        Assert.Equal(ProfitUsd, contract.BookProfitUsd);

        Assert.Equal(FawadContribution, PartnerOf(contract, s.FawadId).FundingUsd);
        Assert.Equal(YusufContribution, PartnerOf(contract, s.YusufId).FundingUsd);
        Assert.Equal(ProfitShareEach, PartnerOf(contract, s.FawadId).ProfitShareUsd);
        Assert.Equal(ProfitShareEach, PartnerOf(contract, s.YusufId).ProfitShareUsd);

        // پرداختِ شرکا دقیقاً همان هزینهٔ دفتری است، پس باقیماندهٔ تطبیق‌نشده صفر می‌ماند.
        Assert.Equal(0m, contract.PaymentToBookDifferenceUsd);
        Assert.Equal(0m, contract.UnreconciledResidualUsd);
    }

    [Fact]
    public async Task AcceptanceScenario_PartnerProfile_ShowsFawadCreditorAndYusufDebtor()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);

        Assert.Equal(FawadNetPosition, fawad.NetPositionUsd);
        Assert.Equal(PartnerBalanceDirection.Creditor, fawad.Direction);
        Assert.Equal(-FawadNetPosition, yusuf.NetPositionUsd);
        Assert.Equal(PartnerBalanceDirection.Debtor, yusuf.Direction);

        // یوسف عاید فروش را نگه داشته: ۴۴۷٬۹۱۱ − ۱۰۲٬۷۴۵٫۵۰ = ۳۴۵٬۱۶۵٫۵۰ بدهی او.
        Assert.Equal(SalesUsd, yusuf.ProceedsHeldUsd);
        Assert.Equal(102_745.50m, yusuf.FundingUsd + yusuf.ProfitShareUsd);

        // مانده شراکت مجموعاً صفر است.
        Assert.Equal(0m, fawad.NetPositionUsd + yusuf.NetPositionUsd);
    }

    [Fact]
    public async Task AcceptanceScenario_PartyStatement_GivesTheSameBalanceAsTheProfile()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var fawad = await BuildPartyStatementAsync(db, s.FawadId);
        var yusuf = await BuildPartyStatementAsync(db, s.YusufId);

        Assert.Equal(FawadNetPosition, fawad.Summary.ClosingBalance);
        Assert.Equal(-FawadNetPosition, yusuf.Summary.ClosingBalance);
    }

    [Fact]
    public async Task AcceptanceScenario_ReceivablesPayables_GivesTheSameBalanceAsTheProfile()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var rows = await BuildBalancesAsync(db);

        Assert.Equal(FawadNetPosition, PartnerRow(rows, s.FawadId).ClosingBalanceUsd);
        Assert.Equal(-FawadNetPosition, PartnerRow(rows, s.YusufId).ClosingBalanceUsd);
    }

    [Fact]
    public async Task AcceptanceScenario_BalanceMeaning_IsWrittenFromThePartnerPointOfView()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var rows = await BuildBalancesAsync(db);

        Assert.Equal("شریک از شراکت طلبکار است", PartnerRow(rows, s.FawadId).BalanceMeaning);
        Assert.Equal("شریک به شراکت بدهکار است", PartnerRow(rows, s.YusufId).BalanceMeaning);
    }

    // ————————————————— هویت شرکت ↔ شریک —————————————————

    [Fact]
    public async Task CompanyFundedPayment_FromTheOwnerPartnersCompany_IsThatPartnersContribution()
    {
        await using var db = CreateDb();
        // همان سناریو، ولی خریدِ فواد از صندوقِ شرکتِ خودش پرداخت شده است.
        var s = await SeedAcceptanceAsync(db, fawadPurchaseFundedByCompany: true, linkCompanyToFawad: true);

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);

        Assert.Equal(FawadContribution, fawad.FundingUsd);
        Assert.Equal(FawadNetPosition, fawad.NetPositionUsd);
        Assert.Equal(-FawadNetPosition, yusuf.NetPositionUsd);
    }

    [Fact]
    public async Task CompanyFundedPayment_WithoutAnOwnerPartner_StaysOutOfEveryPartnerAccount()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db, fawadPurchaseFundedByCompany: true, linkCompanyToFawad: false);

        var fawad = await BuildProfileAsync(db, s.FawadId);

        // ۲۹۶٬۵۷۵ − ۲۷۸٬۰۰۰ = فقط مصارف ترکمنستان از جیب خودش.
        Assert.Equal(TurkmenExpenseUsd, fawad.FundingUsd);
    }

    [Fact]
    public async Task CompanyFundedPayment_WhenTheOwnerPartnerIsNotAContractPartner_IsNotContribution()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db, fawadPurchaseFundedByCompany: true, linkCompanyToFawad: false);

        // مالکِ شرکت یک شریکِ بیرونی است که عضو این قرارداد نیست.
        var outsider = new Partner { Code = "PAR-X", Name = "Outsider", IsActive = true };
        db.Partners.Add(outsider);
        await db.SaveChangesAsync();
        var company = await db.Companies.SingleAsync(c => c.Id == s.CompanyId);
        company.OwnerPartnerId = outsider.Id;
        await db.SaveChangesAsync();

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var outsiderProfile = await new PartnershipStatementService(db).BuildForPartnerAsync(outsider.Id);

        Assert.Equal(TurkmenExpenseUsd, fawad.FundingUsd);
        // مالکِ شرکت هست، ولی عضوِ این قرارداد نیست: نه سرمایه‌ای می‌گیرد و نه مانده‌ای.
        Assert.Empty(outsiderProfile!.Contracts);
        Assert.Equal(0m, outsiderProfile.FundingUsd);
        Assert.Equal(0m, outsiderProfile.NetPositionUsd);
    }

    [Fact]
    public async Task CompanyFundedPayment_OnANonPartnershipContract_IsNotContribution()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db, linkCompanyToFawad: true);

        // یک قرارداد شخصی همان شرکت، با پرداختِ شرکت. فواد اصلاً عضوش نیست.
        var personal = new Contract
        {
            ContractNumber = "P-999",
            ContractName = "قرارداد شخصی",
            ContractType = ContractType.Purchase,
            CompanyId = s.CompanyId,
            ProductId = s.ProductId,
            SupplierId = s.SupplierId,
            OwnershipType = ContractOwnershipType.Personal,
            Currency = "USD",
            QuantityMt = 100m,
            ContractDate = new DateTime(2026, 2, 11)
        };
        db.Contracts.Add(personal);
        await db.SaveChangesAsync();
        await AddPaymentAsync(db, personal.Id, 500_000m, PaymentKind.SupplierPayment, "خرید شخصی",
            new DateTime(2026, 4, 1), fundedByPartnerId: null, companyId: s.CompanyId, supplierId: s.SupplierId);

        var fawad = await BuildProfileAsync(db, s.FawadId);

        Assert.Equal(FawadContribution, fawad.FundingUsd);
        Assert.Equal(FawadNetPosition, fawad.NetPositionUsd);
    }

    [Fact]
    public async Task CompanyFundedPayment_OfAnotherContract_NeverLeaksIntoThisContract()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db, linkCompanyToFawad: true);

        // قرارداد شراکتی دوم با همان دو شریک و پرداختِ شرکت.
        var second = await AddPartnershipContractAsync(db, s, "P-777");
        await AddPaymentAsync(db, second, 90_000m, PaymentKind.SupplierPayment, "خرید قرارداد دوم",
            new DateTime(2026, 5, 1), fundedByPartnerId: null, companyId: s.CompanyId, supplierId: s.SupplierId);

        var statement = await new PartnershipStatementService(db).BuildAsync(s.FawadId, s.YusufId, [s.ContractId]);
        Assert.NotNull(statement);
        var first = statement!.Contracts.Single();

        Assert.Equal(FawadContribution, PartnerOf(first, s.FawadId).FundingUsd);
        Assert.Equal(YusufContribution, PartnerOf(first, s.YusufId).FundingUsd);
    }

    // ————————————————— پرداخت، دریافت و تسویه —————————————————

    [Fact]
    public async Task PartnerFundedPayment_CountsOnlyForThePayingPartner()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);

        Assert.Equal(FawadContribution, fawad.FundingUsd);
        Assert.Equal(YusufContribution, yusuf.FundingUsd);
    }

    [Fact]
    public async Task IncomingPartnerPayment_ReducesThatPartnersContribution()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        // برگشتِ ۵٬۰۰۰ از تأمین‌کننده به فواد — پرداخت نیست، اصلاح است.
        await AddPaymentAsync(db, s.ContractId, 5_000m, PaymentKind.SupplierReceipt, "برگشت وجه",
            new DateTime(2026, 8, 25), fundedByPartnerId: s.FawadId, companyId: null,
            supplierId: s.SupplierId, direction: PaymentDirection.In);

        var fawad = await BuildProfileAsync(db, s.FawadId);

        Assert.Equal(FawadContribution - 5_000m, fawad.FundingUsd);
        Assert.Equal(FawadNetPosition - 5_000m, fawad.NetPositionUsd);
    }

    [Fact]
    public async Task PartialSettlement_MovesBothBalancesByExactlyTheSettledAmount()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);
        await AddSettlementAsync(db, s, from: s.YusufId, to: s.FawadId, amountUsd: 100_000m);

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);
        var rows = await BuildBalancesAsync(db);

        Assert.Equal(FawadNetPosition - 100_000m, fawad.NetPositionUsd);
        Assert.Equal(-(FawadNetPosition - 100_000m), yusuf.NetPositionUsd);
        Assert.Equal(fawad.NetPositionUsd, PartnerRow(rows, s.FawadId).ClosingBalanceUsd);
        Assert.Equal(yusuf.NetPositionUsd, PartnerRow(rows, s.YusufId).ClosingBalanceUsd);
    }

    [Fact]
    public async Task FullSettlement_ClearsBothPartners()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);
        await AddSettlementAsync(db, s, from: s.YusufId, to: s.FawadId, amountUsd: FawadNetPosition);

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);

        Assert.Equal(0m, fawad.NetPositionUsd);
        Assert.Equal(0m, yusuf.NetPositionUsd);
        Assert.Equal(PartnerBalanceDirection.Settled, fawad.Direction);
        Assert.Equal(PartnerBalanceDirection.Settled, yusuf.Direction);
    }

    [Fact]
    public async Task ReversedSettlement_LeavesTheBalanceUntouched()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);
        var settlement = await AddSettlementAsync(db, s, from: s.YusufId, to: s.FawadId, amountUsd: 100_000m);
        settlement.IsReversed = true;
        settlement.ReversalReason = "ثبت اشتباه";
        await db.SaveChangesAsync();

        var fawad = await BuildProfileAsync(db, s.FawadId);
        var rows = await BuildBalancesAsync(db);

        Assert.Equal(FawadNetPosition, fawad.NetPositionUsd);
        Assert.Equal(FawadNetPosition, PartnerRow(rows, s.FawadId).ClosingBalanceUsd);
    }

    // ————————————————— دوبار شمردن و مرزها —————————————————

    [Fact]
    public async Task PartnerFundedPayment_IsNotCountedAsCompanyCashOutflow()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var company = await BuildPartyStatementAsync(db, s.CompanyId, PartyStatementPartyType.Company);

        // هیچ سطرِ روزنامچهٔ شریک در حساب جاری جواز نیست؛ فقط رویدادهای واقعی شرکت.
        Assert.DoesNotContain(company.Rows, r => r.SourceType is "SupplierPayment" or "ServiceProviderPayment");
    }

    [Fact]
    public async Task CompanyStatement_StillContainsTheExpenseItself()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var company = await BuildPartyStatementAsync(db, s.CompanyId, PartyStatementPartyType.Company);

        // اصلِ مصرف حذف نشده — فقط پرداختِ شریک از حساب جواز بیرون رفته است.
        var expenseRow = Assert.Single(company.Rows.Where(r => r.SourceType == "Expense"));
        Assert.Equal(ContractExpenseUsd, expenseRow.ReceiptBase ?? expenseRow.OutflowBase);
    }

    [Fact]
    public async Task NonPartnershipContracts_ProduceNoPartnerBalanceRow()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        // قرارداد را شخصی می‌کنیم: هیچ ماندهٔ شراکتی نباید بماند.
        var contract = await db.Contracts.SingleAsync(c => c.Id == s.ContractId);
        contract.OwnershipType = ContractOwnershipType.Personal;
        await db.SaveChangesAsync();

        var rows = await BuildBalancesAsync(db);
        var profile = await new PartnershipStatementService(db).BuildForPartnerAsync(s.FawadId);

        Assert.DoesNotContain(rows, r => r.PartyType == PartyStatementPartyType.Partner);
        Assert.Equal(0m, profile!.NetPositionUsd);
    }

    [Fact]
    public async Task PartnerStatement_InANonUsdCurrency_ReturnsNothingInsteadOfAMadeUpNumber()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db);

        var statement = await BuildPartyStatementAsync(
            db, s.FawadId, PartyStatementPartyType.Partner, new PartyStatementFilter { CurrencyCode = "AFN" });

        Assert.DoesNotContain(statement.Rows, r => !r.IsOpeningBalance);
        Assert.Equal(0m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task PartnerStatementFilteredToOneContract_MatchesThatContractPosition()
    {
        await using var db = CreateDb();
        var s = await SeedAcceptanceAsync(db, linkCompanyToFawad: true);
        var second = await AddPartnershipContractAsync(db, s, "P-778");
        await AddPaymentAsync(db, second, 40_000m, PaymentKind.SupplierPayment, "خرید قرارداد دوم",
            new DateTime(2026, 5, 1), fundedByPartnerId: s.FawadId, companyId: null, supplierId: s.SupplierId);

        var all = await BuildProfileAsync(db, s.FawadId);
        var firstOnly = await new PartnershipStatementService(db)
            .BuildForPartnerAsync(s.FawadId, [s.ContractId]);

        Assert.Equal(FawadContribution + 40_000m, all.FundingUsd);
        Assert.Equal(FawadContribution, firstOnly!.FundingUsd);
        Assert.Equal(FawadNetPosition, firstOnly.NetPositionUsd);
    }

    // ————————————————— کمک‌کننده‌ها —————————————————

    private sealed record Scenario(
        int CompanyId,
        int ProductId,
        int SupplierId,
        int ContractId,
        int FawadId,
        int YusufId,
        int CashAccountId,
        int ExpenseTypeId);

    private static PartnershipPartnerTotals PartnerOf(PartnershipContractStatement contract, int partnerId)
        => contract.Partners.Single(p => p.PartnerId == partnerId);

    private static PartyBalanceSnapshot PartnerRow(IEnumerable<PartyBalanceSnapshot> rows, int partnerId)
        => rows.Single(r => r.PartyType == PartyStatementPartyType.Partner && r.PartyId == partnerId);

    private static async Task<PartnerAccountStatement> BuildProfileAsync(ApplicationDbContext db, int partnerId)
    {
        var statement = await new PartnershipStatementService(db).BuildForPartnerAsync(partnerId);
        Assert.NotNull(statement);
        return statement!;
    }

    private static Task<PartyStatementResult> BuildPartyStatementAsync(
        ApplicationDbContext db,
        int partyId,
        PartyStatementPartyType partyType = PartyStatementPartyType.Partner,
        PartyStatementFilter? filter = null)
        => new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()),
                new PartyDirectory(db))
            .GetStatementAsync(
                new PartyRef(partyType, partyId),
                filter ?? new PartyStatementFilter { IncludeOperationalColumns = false });

    private static Task<IReadOnlyList<PartyBalanceSnapshot>> BuildBalancesAsync(ApplicationDbContext db)
        => new PartyBalanceReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                new PartyDirectory(db))
            .GetBalancesAsync(new ManagementReportFilterViewModel());

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// قرارداد ۵۰۰ تن با همان اعداد توافق‌شده. هیچ عددی گرد یا تخمینی نیست.
    /// </summary>
    private static async Task<Scenario> SeedAcceptanceAsync(
        ApplicationDbContext db,
        bool fawadPurchaseFundedByCompany = false,
        bool linkCompanyToFawad = false)
    {
        var company = new Company { Code = "PTG", Name = "PTG", IsSystemOwner = true };
        var product = new Product { Code = "MO", Name = "Base Oil" };
        var supplier = new Supplier { Name = "Refinery", IsActive = true };
        var customer = new Customer { Name = "Buyer", IsActive = true };
        var fawad = new Partner { Code = "PAR-F", Name = "فواد صدیقی", IsActive = true };
        var yusuf = new Partner { Code = "PAR-Y", Name = "یوسف اسماعیل", IsActive = true };
        var cash = new CashAccount { Code = "CASH", Name = "Main Cash", Currency = "USD", IsActive = true };
        var expenseType = new ExpenseType { Code = "OPS", Name = "مصارف قرارداد" };
        db.AddRange(company, product, supplier, customer, fawad, yusuf, cash, expenseType);
        await db.SaveChangesAsync();

        if (linkCompanyToFawad)
        {
            company.OwnerPartnerId = fawad.Id;
            await db.SaveChangesAsync();
        }

        var contract = new Contract
        {
            ContractNumber = "P-016",
            ContractName = "۵۰۰ تن مبلایل شراکتی",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            OwnershipType = ContractOwnershipType.Partnership,
            Currency = "USD",
            QuantityMt = 500m,
            ContractDate = new DateTime(2026, 2, 11)
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        contract.SaleProceedsHolderPartnerId = yusuf.Id;
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = contract.Id, PartnerId = yusuf.Id, SharePercent = 50m });

        // خرید دفتری: ۵۰۰ تن × ۵۵۶ = ۲۷۸٬۰۰۰
        db.LoadingRegisters.Add(new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = product.Id,
            LoadingDate = new DateTime(2026, 3, 20),
            LoadedQuantityMt = 500m,
            LoadingPriceUsd = 556m
        });

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = contract.Id,
            ExpenseDate = new DateTime(2026, 4, 20),
            Amount = ContractExpenseUsd,
            Currency = "USD",
            AmountUsd = ContractExpenseUsd,
            Description = "مصارف قرارداد"
        };
        db.ExpenseTransactions.Add(expense);

        var sale = new SalesTransaction
        {
            CompanyId = company.Id,
            ProductId = product.Id,
            CustomerId = customer.Id,
            InvoiceNumber = "GSALE-10-1",
            SaleDate = new DateTime(2026, 8, 22),
            QuantityMt = 500m,
            UnitPriceUsd = SalesUsd / 500m,
            TotalUsd = SalesUsd,
            Currency = "USD",
            TotalInCurrency = SalesUsd,
            AppliedFxRateToUsd = 1m
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();

        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 8, 22),
                Side = LedgerSide.Credit,
                AmountUsd = SalesUsd,
                Currency = "USD",
                ContractId = contract.Id,
                SourceType = "Sale",
                SourceId = sale.Id,
                Description = "Sale"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 4, 20),
                Side = LedgerSide.Debit,
                AmountUsd = ContractExpenseUsd,
                Currency = "USD",
                ContractId = contract.Id,
                SourceType = "Expense",
                SourceId = expense.Id,
                Description = "مصارف قرارداد"
            });
        await db.SaveChangesAsync();

        var scenario = new Scenario(
            company.Id, product.Id, supplier.Id, contract.Id, fawad.Id, yusuf.Id, cash.Id, expenseType.Id);

        await AddPaymentAsync(db, contract.Id, PurchaseUsd, PaymentKind.SupplierPayment, "خرید",
            new DateTime(2026, 3, 21),
            fundedByPartnerId: fawadPurchaseFundedByCompany ? null : fawad.Id,
            companyId: fawadPurchaseFundedByCompany ? company.Id : null,
            supplierId: supplier.Id,
            cashAccountId: fawadPurchaseFundedByCompany ? cash.Id : null);
        await AddPaymentAsync(db, contract.Id, TurkmenExpenseUsd, PaymentKind.ServiceProviderPayment,
            "مصارف ترکمنستان", new DateTime(2026, 4, 5), fundedByPartnerId: fawad.Id, companyId: null);
        await AddPaymentAsync(db, contract.Id, FreightUsd, PaymentKind.ServiceProviderPayment,
            "کرایه", new DateTime(2026, 4, 6), fundedByPartnerId: yusuf.Id, companyId: null);
        await AddPaymentAsync(db, contract.Id, TruckWaitingUsd, PaymentKind.ServiceProviderPayment,
            "شب‌خواب موترها", new DateTime(2026, 4, 7), fundedByPartnerId: yusuf.Id, companyId: null);
        await AddPaymentAsync(db, contract.Id, CustomsUsd, PaymentKind.ServiceProviderPayment,
            "گمرک", new DateTime(2026, 4, 8), fundedByPartnerId: yusuf.Id, companyId: null);

        return scenario;
    }

    private static async Task<int> AddPartnershipContractAsync(
        ApplicationDbContext db,
        Scenario s,
        string number)
    {
        var contract = new Contract
        {
            ContractNumber = number,
            ContractName = number,
            ContractType = ContractType.Purchase,
            CompanyId = s.CompanyId,
            ProductId = s.ProductId,
            SupplierId = s.SupplierId,
            OwnershipType = ContractOwnershipType.Partnership,
            Currency = "USD",
            QuantityMt = 100m,
            ContractDate = new DateTime(2026, 2, 11)
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = s.FawadId, SharePercent = 50m },
            new ContractPartner { ContractId = contract.Id, PartnerId = s.YusufId, SharePercent = 50m });
        await db.SaveChangesAsync();
        return contract.Id;
    }

    /// <summary>
    /// یک پرداخت با سندِ روزنامچه‌اش. <paramref name="fundedByPartnerId"/> خالی یعنی پرداختِ شرکت.
    /// </summary>
    private static async Task AddPaymentAsync(
        ApplicationDbContext db,
        int contractId,
        decimal amountUsd,
        PaymentKind kind,
        string description,
        DateTime date,
        int? fundedByPartnerId,
        int? companyId,
        int? supplierId = null,
        int? cashAccountId = null,
        PaymentDirection direction = PaymentDirection.Out)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = date,
            Direction = direction,
            PaymentKind = kind,
            ContractId = contractId,
            CompanyId = companyId,
            SupplierId = supplierId,
            CashAccountId = cashAccountId,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = description,
            FundingSource = fundedByPartnerId.HasValue
                ? PaymentFundingSource.Partner
                : PaymentFundingSource.Company,
            PaidByPartnerId = fundedByPartnerId
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        var ledger = new LedgerEntry
        {
            EntryDate = date,
            Side = direction == PaymentDirection.Out ? LedgerSide.Debit : LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            SupplierId = supplierId,
            SourceType = kind.ToString(),
            SourceId = payment.Id,
            Description = description
        };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();

        payment.LedgerEntryId = ledger.Id;
        await db.SaveChangesAsync();
    }

    private static async Task<PartnerSettlement> AddSettlementAsync(
        ApplicationDbContext db,
        Scenario s,
        int from,
        int to,
        decimal amountUsd)
    {
        var settlement = new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 9, 1),
            FromPartnerId = from,
            ToPartnerId = to,
            ContractId = s.ContractId,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = "تسویه بین شرکا"
        };
        db.PartnerSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }
}
