using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Partners;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// پروفایل شریک — دو تب، یک منبع حقیقت.
///
/// همان دو قرارداد واقعی P-016 و P-017: هر عددی که پروفایل نشان می‌دهد باید با
/// <see cref="PartnershipStatementService"/> — یعنی همان صورت‌حساب شراکت — تطبیق شود،
/// و باز کردن صفحه نباید هیچ سندی بسازد یا مفاد قرارداد را تکان دهد.
/// </summary>
public sealed class PartnerProfileTests
{
    // ————————————————— ۱: فقط دو تب اصلی —————————————————

    [Fact]
    public void PartnerDetails_HasExactlyTwoMainTabs()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");

        Assert.Contains("خلاصه حساب", view);
        Assert.Contains("گردش حساب", view);

        // ریلِ تب فقط دو توصیف‌گر دارد؛ تب سوم یعنی صفحه دوباره شلوغ شده است.
        var railStart = view.IndexOf("var partnerTabs = new[]", StringComparison.Ordinal);
        Assert.True(railStart >= 0);
        var rail = view[railStart..view.IndexOf("};", railStart, StringComparison.Ordinal)];
        Assert.Equal(2, Count(rail, "|bi-"));

        // تب‌های حذف‌شدهٔ نسخهٔ قبلی برنگردند.
        Assert.DoesNotContain("حساب شریک|", view);
        Assert.DoesNotContain("معاملات و سود/زیان", view);

        // هیچ ورودی‌ای نمی‌تواند تب سوم بسازد.
        Assert.Equal(PartnerProfileTabs.Summary, PartnerProfileTabs.Resolve(null));
        Assert.Equal(PartnerProfileTabs.Summary, PartnerProfileTabs.Resolve("contracts"));
        Assert.Equal(PartnerProfileTabs.Summary, PartnerProfileTabs.Resolve("account"));
        Assert.Equal(PartnerProfileTabs.Ledger, PartnerProfileTabs.Resolve("ledger"));
    }

    [Fact]
    public void PartnerDetails_DoesNotShowASecondParallelBalance()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");

        // نه صورت‌حساب رسمی داخل صفحه جاسازی می‌شود و نه اصطلاح خام حسابداری نمایش داده می‌شود.
        Assert.DoesNotContain("data-party-statement-embed", view);
        Assert.DoesNotContain("PartyStatementSummary", view);
        Assert.DoesNotContain("StatementBalanceUsd", view);
        Assert.DoesNotContain("CalculatedBalance", view);

        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/PartnersController.cs");
        // فرمول موازیِ قدیمیِ مانده در کنترلر باقی نمانده باشد.
        Assert.DoesNotContain("SignedEffect", controller);
        Assert.DoesNotContain("ICompanyFlowDirectionResolver", controller);
        Assert.DoesNotContain("IPartyStatementReadService", controller);
        Assert.Contains("BuildForPartnerAsync", controller);
    }

    [Fact]
    public void BalanceSentence_UsesTheFullPartnerName()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");

        // جملهٔ مانده با نام کامل شریک نوشته می‌شود، نه با «این شریک».
        Assert.Contains("var partnerFullName", view);
        Assert.Contains("{partnerFullName} باید", view);
        Assert.Contains("{partnerFullName} {NumberDisplay.Money(balanceAmount)} USD طلبکار است", view);
        // جملهٔ مانده دیگر بی‌نام نیست (متن حالت خالیِ جدول قرارداد استثناست).
        Assert.DoesNotContain("این شریک {NumberDisplay", view);
        Assert.DoesNotContain("این شریک باید", view);
    }

    [Fact]
    public async Task ProfileKeepsEachPartnersOwnNetPosition()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var fawad = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var yusuf = (await BuildProfileAsync(db, s.YusufId)).Statement!;
        var pair = await BuildPairAsync(db, s);

        // پروفایل هر شریک مانده واقعی خودش را نگه می‌دارد؛ باقیماندهٔ تطبیق‌نشده بین آن دو
        // پخش یا میانگین‌گیری نمی‌شود.
        Assert.NotEqual(fawad.AmountUsd, yusuf.AmountUsd);
        Assert.Equal(
            pair.UnreconciledResidualUsd,
            decimal.Round(fawad.NetPositionUsd + yusuf.NetPositionUsd, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(pair.AmountDueUsd, fawad.AmountUsd);
        Assert.Equal(pair.CreditorClaimUsd, yusuf.AmountUsd);
    }

    // ————————————————— ۲ و ۳: یک منبع حقیقت و تطبیق با صورت‌حساب شراکت —————————————————

    [Fact]
    public async Task ProfileBalance_ComesFromTheSameSourceAsThePartnershipStatement()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var pair = await BuildPairAsync(db, s);
        var fawad = await BuildProfileAsync(db, s.FawadId);
        var yusuf = await BuildProfileAsync(db, s.YusufId);

        var pairFawad = pair.Totals.Single(t => t.PartnerId == s.FawadId);
        var pairYusuf = pair.Totals.Single(t => t.PartnerId == s.YusufId);

        Assert.Equal(pairFawad.NetPositionUsd, fawad.Statement!.NetPositionUsd);
        Assert.Equal(pairYusuf.NetPositionUsd, yusuf.Statement!.NetPositionUsd);
        Assert.Equal(pairFawad.FundingUsd, fawad.Statement.FundingUsd);
        Assert.Equal(pairFawad.ProceedsHeldUsd, fawad.Statement.ProceedsHeldUsd);
        Assert.Equal(pairFawad.ProfitShareUsd, fawad.Statement.ProfitShareUsd);
        Assert.Equal(pairYusuf.FundingUsd, yusuf.Statement.FundingUsd);
        Assert.Equal(pairYusuf.ProceedsHeldUsd, yusuf.Statement.ProceedsHeldUsd);
        Assert.Equal(pairYusuf.ProfitShareUsd, yusuf.Statement.ProfitShareUsd);
    }

    [Fact]
    public async Task ProfileDirection_MatchesTheDebtorAndCreditorOfThePartnershipStatement()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var pair = await BuildPairAsync(db, s);
        var debtor = await BuildProfileAsync(db, pair.DebtorPartnerId!.Value);
        var creditor = await BuildProfileAsync(db, pair.CreditorPartnerId!.Value);

        Assert.Equal(PartnerBalanceDirection.Debtor, debtor.Statement!.Direction);
        Assert.Equal(PartnerBalanceDirection.Creditor, creditor.Statement!.Direction);
        Assert.Equal(pair.AmountDueUsd, debtor.Statement.AmountUsd);
        Assert.Equal(pair.CreditorClaimUsd, creditor.Statement.AmountUsd);
    }

    [Fact]
    public async Task LedgerRunningBalance_EndsExactlyOnTheProfileBalance()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        foreach (var partnerId in new[] { s.FawadId, s.YusufId })
        {
            var profile = await BuildProfileAsync(db, partnerId);
            var entries = profile.Statement!.Entries;

            Assert.NotEmpty(entries);
            Assert.Equal(profile.Statement.NetPositionUsd, entries[^1].RunningBalanceUsd);
            Assert.Equal(
                profile.Statement.NetPositionUsd,
                decimal.Round(entries.Sum(e => e.EffectUsd), 2, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public async Task ContractRows_SumToTheProfileBalance()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var profile = await BuildProfileAsync(db, s.FawadId);
        var statement = profile.Statement!;

        Assert.Equal(2, statement.Contracts.Count);
        Assert.Equal(
            statement.NetPositionUsd,
            decimal.Round(statement.Contracts.Sum(c => c.NetPositionUsd), 2, MidpointRounding.AwayFromZero));
    }

    // ————————————————— ۴: تغییر پرداخت شریک روی پروفایل دیده می‌شود —————————————————

    [Fact]
    public async Task NewPartnerFunding_MovesProfileBalanceByExactlyThatAmount()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = (await BuildProfileAsync(db, s.FawadId)).Statement!;

        AddFunding(db, s.Contract16Id, s.FawadId, 50_000m, PaymentKind.ServiceProviderPayment, "پرداخت اضافی شریک");
        await db.SaveChangesAsync();

        var after = (await BuildProfileAsync(db, s.FawadId)).Statement!;

        Assert.Equal(before.FundingUsd + 50_000m, after.FundingUsd);
        Assert.Equal(before.NetPositionUsd + 50_000m, after.NetPositionUsd);
        Assert.Contains(after.Entries, e => e.AmountUsd == 50_000m
            && e.Kind == PartnershipStatementLineKind.PartnerExpense
            && e.EffectUsd == 50_000m);
    }

    // ————————————————— ۵: نگهدارندهٔ عاید فروش روی پروفایل دیده می‌شود —————————————————

    [Fact]
    public async Task SaleProceedsHolder_IsVisibleOnTheHoldersProfileOnly()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var fawad = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var yusuf = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        var fawad17 = fawad.Contracts.Single(c => c.ContractId == s.Contract17Id);
        var yusuf17 = yusuf.Contracts.Single(c => c.ContractId == s.Contract17Id);
        var fawad16 = fawad.Contracts.Single(c => c.ContractId == s.Contract16Id);
        var yusuf16 = yusuf.Contracts.Single(c => c.ContractId == s.Contract16Id);

        // P-017 نزد فواد، P-016 نزد یوسف — همان چیزی که روی قرارداد ثبت شده است.
        Assert.True(fawad17.ProceedsHeldUsd > 0m);
        Assert.Equal(0m, yusuf17.ProceedsHeldUsd);
        Assert.True(yusuf16.ProceedsHeldUsd > 0m);
        Assert.Equal(0m, fawad16.ProceedsHeldUsd);

        // عاید نزد شریک، حساب او را بدهکار می‌کند؛ اثرش در گردش حساب منفی است.
        var proceedsRow = fawad.Entries.Single(e =>
            e.Kind == PartnershipStatementLineKind.SaleProceedsHeld && e.ContractId == s.Contract17Id);
        Assert.Equal(-fawad17.ProceedsHeldUsd, proceedsRow.EffectUsd);
    }

    [Fact]
    public async Task MultipleSalesPerContract_ShowAsOneAggregatedLedgerRow()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var companyId = await db.Companies.Select(c => c.Id).SingleAsync();
        var productId = await db.Products.Select(p => p.Id).SingleAsync();
        var customerId = await db.Customers.Select(c => c.Id).SingleAsync();

        var extraSale = NewSale(companyId, productId, customerId, "GSALE-11-2", 11.6517m, 9_200m, s.Contract17Id);
        db.SalesTransactions.Add(extraSale);
        await db.SaveChangesAsync();

        var statement = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var saleRows = statement.Entries
            .Where(e => e.Kind == PartnershipStatementLineKind.SaleProceedsHeld && e.ContractId == s.Contract17Id)
            .ToList();

        var proceedsHeld = statement.Contracts.Single(c => c.ContractId == s.Contract17Id).ProceedsHeldUsd;
        var row = Assert.Single(saleRows);
        Assert.Equal("جمع عاید فروش (2 فروش)", row.Description);
        Assert.Equal(proceedsHeld, row.AmountUsd);
        Assert.Equal(-proceedsHeld, row.EffectUsd);
        Assert.Equal("Contract", row.SourceType);
        Assert.Equal(s.Contract17Id, row.SourceId);
        Assert.Equal(proceedsHeld, row.CreditUsd);
    }

    [Fact]
    public async Task MovingTheProceedsHolder_MovesItOnBothProfiles()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var contract = await db.Contracts.SingleAsync(c => c.Id == s.Contract16Id);
        var proceeds = (await BuildProfileAsync(db, s.YusufId)).Statement!
            .Contracts.Single(c => c.ContractId == s.Contract16Id).ProceedsHeldUsd;

        contract.SaleProceedsHolderPartnerId = s.FawadId;
        await db.SaveChangesAsync();

        var fawad = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var yusuf = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        Assert.Equal(proceeds, fawad.Contracts.Single(c => c.ContractId == s.Contract16Id).ProceedsHeldUsd);
        Assert.Equal(0m, yusuf.Contracts.Single(c => c.ContractId == s.Contract16Id).ProceedsHeldUsd);
    }

    // ————————————————— ۶: تسویه روی پروفایل و گردش حساب —————————————————

    [Fact]
    public async Task Settlement_ShowsOnProfileSummaryAndLedger()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = (await BuildProfileAsync(db, s.FawadId)).Statement!;

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            Amount = 100_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100_000m
        });
        await db.SaveChangesAsync();

        var payer = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var receiver = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        Assert.Equal(100_000m, payer.SettlementsPaidUsd);
        Assert.Equal(100_000m, receiver.SettlementsReceivedUsd);
        Assert.Equal(before.NetPositionUsd + 100_000m, payer.NetPositionUsd);

        var payerRow = payer.Entries.Single(e => e.Kind == PartnershipStatementLineKind.PartnerSettlement);
        var receiverRow = receiver.Entries.Single(e => e.Kind == PartnershipStatementLineKind.PartnerSettlement);
        Assert.Equal(100_000m, payerRow.EffectUsd);
        Assert.Equal(-100_000m, receiverRow.EffectUsd);
        Assert.Equal(payer.NetPositionUsd, payer.Entries[^1].RunningBalanceUsd);
    }

    [Fact]
    public async Task ReversedSettlement_StaysInTheLedgerButNotInTheBalance()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = (await BuildProfileAsync(db, s.FawadId)).Statement!;

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            Amount = 100_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100_000m,
            IsReversed = true,
            ReversedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var after = (await BuildProfileAsync(db, s.FawadId)).Statement!;

        Assert.Equal(before.NetPositionUsd, after.NetPositionUsd);
        Assert.Equal(0m, after.SettlementsPaidUsd);
        var reversal = after.Entries.Single(e => e.Kind == PartnershipStatementLineKind.Adjustment);
        Assert.Equal(0m, reversal.EffectUsd);
    }

    [Fact]
    public async Task GeneralSettlement_WithoutContract_StaysVisibleAndIsNamed()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 23),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            ContractId = null,
            Amount = 40_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 40_000m
        });
        await db.SaveChangesAsync();

        // تسویهٔ کلیِ حساب قرارداد ندارد، ولی در نمای پیش‌فرض (همهٔ قراردادها) دیده می‌شود.
        var profile = await BuildProfileAsync(db, s.FawadId);
        var row = profile.Entries.Single(e => e.Kind == PartnershipStatementLineKind.PartnerSettlement);
        Assert.Null(row.ContractId);
        Assert.Null(row.ContractLabel);
        Assert.Equal(40_000m, row.EffectUsd);

        // و ستون قرارداد به‌جای خط تیرهٔ بی‌معنی، عنوان می‌گیرد.
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");
        Assert.Contains("var generalAccountLabel = \"تسویه عمومی\";", view);
        Assert.Contains("@(entry.ContractLabel ?? generalAccountLabel)", view);
        Assert.DoesNotContain("@(entry.ContractLabel ?? \"-\")", view);
    }

    [Fact]
    public async Task LossShare_LandsOnThePartnerAccountWithTheSameFormula()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var beforeC16 = before.Contracts.Single(c => c.ContractId == s.Contract16Id);
        Assert.True(beforeC16.ProfitShareUsd > 0m);

        // مصرفِ سنگین قرارداد P-016 را از مفاد به ضرر می‌برد.
        var expenseTypeId = await db.ExpenseTypes.Select(t => t.Id).FirstAsync();
        db.ExpenseTransactions.Add(
            NewExpense(expenseTypeId, s.Contract16Id, new DateTime(2026, 7, 1), 400_000m));
        await db.SaveChangesAsync();

        var after = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        var afterC16 = after.Contracts.Single(c => c.ContractId == s.Contract16Id);

        // سهم ضرر همان سهم مفاد با علامت منفی است — فرمول دوم ساخته نشده.
        // (اختلاف تا یک سِنت فقط گِردکردنِ همان یک فرمول است، نه فرمول دیگر.)
        var partnerLoss = 400_000m * afterC16.SharePercent / 100m;
        Assert.True(afterC16.ProfitShareUsd < 0m);
        Assert.True(Math.Abs(beforeC16.ProfitShareUsd - partnerLoss - afterC16.ProfitShareUsd) <= 0.01m);

        // و مانده شریک دقیقاً به همان اندازه پایین می‌آید.
        Assert.True(Math.Abs(before.NetPositionUsd - partnerLoss - after.NetPositionUsd) <= 0.01m);
        Assert.Equal(after.NetPositionUsd, after.Entries[^1].RunningBalanceUsd);
    }

    [Fact]
    public async Task SettlementDrillDown_UsesTheCounterpartyOfThatSettlement()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        // شریک سوم و چهارم، روی یک قرارداد تازه — تا فهرست شرکای مقابل بیش از یک نفر باشد.
        var third = new Partner { Code = "PAR-3", Name = "شریک سوم", IsActive = true };
        var fourth = new Partner { Code = "PAR-4", Name = "شریک چهارم", IsActive = true };
        db.AddRange(third, fourth);
        await db.SaveChangesAsync();

        var head = await db.Contracts.AsNoTracking().FirstAsync(c => c.Id == s.Contract16Id);
        var c18 = NewPartnershipContract(head.CompanyId, head.ProductId, head.SupplierId!.Value, "P-018", "قرارداد سه‌نفره", 100m);
        db.Contracts.Add(c18);
        await db.SaveChangesAsync();

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = c18.Id, PartnerId = s.FawadId, SharePercent = 40m },
            new ContractPartner { ContractId = c18.Id, PartnerId = third.Id, SharePercent = 30m },
            new ContractPartner { ContractId = c18.Id, PartnerId = fourth.Id, SharePercent = 30m });

        // دو تسویه با دو طرفِ متفاوت: یکی پرداختی، یکی دریافتی.
        db.PartnerSettlements.AddRange(
            new PartnerSettlement
            {
                SettlementDate = new DateTime(2026, 8, 24),
                FromPartnerId = s.FawadId,
                ToPartnerId = fourth.Id,
                ContractId = c18.Id,
                Amount = 10_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 10_000m
            },
            new PartnerSettlement
            {
                SettlementDate = new DateTime(2026, 8, 25),
                FromPartnerId = third.Id,
                ToPartnerId = s.FawadId,
                ContractId = c18.Id,
                Amount = 4_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 4_000m
            });
        await db.SaveChangesAsync();

        var profile = await BuildProfileAsync(db, s.FawadId);

        // شریک مقابل بیش از یکی است، پس «اولین شریک» دیگر جواب درستی نیست.
        Assert.True(profile.CoPartners.Count >= 3);
        var firstCoPartnerId = profile.CoPartners[0].PartnerId;

        var paid = profile.Entries.Single(e => e.SourceType == "Settlement" && e.EffectUsd == 10_000m);
        var received = profile.Entries.Single(e => e.SourceType == "Settlement" && e.EffectUsd == -4_000m);

        // هر ردیف طرفِ واقعیِ همان تسویه را می‌برد.
        Assert.Equal(fourth.Id, paid.CounterpartyPartnerId);
        Assert.Equal(third.Id, received.CounterpartyPartnerId);
        Assert.NotEqual(paid.CounterpartyPartnerId, received.CounterpartyPartnerId);

        // و دست‌کم یکی از آن‌ها با «اولین شریک مقابل» فرق دارد — همان باگی که رفع شد.
        Assert.True(paid.CounterpartyPartnerId != firstCoPartnerId
            || received.CounterpartyPartnerId != firstCoPartnerId);

        // لینک درل‌داون از همین فیلد ساخته می‌شود، نه از CoPartners.FirstOrDefault().
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");
        Assert.Contains("partnerBId = entry.CounterpartyPartnerId.Value", view);
        Assert.DoesNotContain("Model.CoPartners.FirstOrDefault()?.PartnerId", view);
    }

    // ————————————————— گردش حساب به سبک دفتر حسابدار —————————————————

    [Fact]
    public void Ledger_HasTheAccountantsColumns()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");

        var headStart = view.IndexOf("<th>تاریخ</th>", StringComparison.Ordinal);
        Assert.True(headStart >= 0);
        var head = view[headStart..view.IndexOf("</thead>", headStart, StringComparison.Ordinal)];

        foreach (var column in new[] { "تاریخ", "قرارداد", "شرح", "مقدار MT", "نرخ", "بردگی", "رسیدگی", "مانده" })
        {
            Assert.Contains($">{column}</th>", head);
        }

        // ستون‌های قدیمیِ «مبلغ/اثر» جای خود را به رسیدگی/بردگی داده‌اند.
        Assert.DoesNotContain("مبلغ USD", head);
        Assert.DoesNotContain("اثر بر حساب", head);

        // بالای تب فقط سه عدد: مجموع بردگی، مجموع رسیدگی، مانده فعلی.
        var kpiStart = view.IndexOf("var ledgerKpis = new List<AkKpiItem>", StringComparison.Ordinal);
        Assert.True(kpiStart >= 0);
        var kpis = view[kpiStart..view.IndexOf("};", kpiStart, StringComparison.Ordinal)];
        Assert.Equal(3, Count(kpis, "new() { Title ="));
        Assert.Contains("مجموع بردگی", kpis);
        Assert.Contains("مجموع رسیدگی", kpis);
        Assert.Contains("مانده فعلی", kpis);
    }

    [Fact]
    public async Task EveryLedgerRow_FillsExactlyOneOfDebitOrCredit()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.FawadId)).Statement!;
        Assert.NotEmpty(statement.Entries);

        foreach (var entry in statement.Entries)
        {
            // هیچ ردیفی هم‌زمان بدهکار و بستانکار نیست، و هیچ ستونی منفی نمی‌شود.
            Assert.True(entry.CreditUsd >= 0m && entry.DebitUsd >= 0m);
            Assert.True(entry.CreditUsd == 0m || entry.DebitUsd == 0m);
            // نگاشتِ دفترِ حسابدار: آنچه شریک آورده بدهکار است، پس اثرِ داخلی = بدهکار − بستانکار.
            Assert.Equal(entry.EffectUsd, entry.DebitUsd - entry.CreditUsd);
        }

        // و تفکیک بدهکار/بستانکار با همان NetPosition سرویس تطبیق می‌شود.
        Assert.Equal(statement.NetPositionUsd, statement.TotalDebitUsd - statement.TotalCreditUsd);
        Assert.Equal(statement.NetPositionUsd, statement.Entries[^1].RunningBalanceUsd);

        // و ماندهٔ نمایشی همان «بستانکار − بدهکار» است — قاعدهٔ دفترِ حسابدار.
        Assert.Equal(statement.TotalCreditUsd - statement.TotalDebitUsd, statement.AccountantBalanceUsd);
        Assert.Equal(statement.AccountantBalanceUsd, statement.Entries[^1].AccountantBalanceUsd);
    }

    [Fact]
    public async Task QuantityAndRate_ComeFromTheDocumentOrStayEmpty()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        // عاید فروش نزد شریک: سند فروش مقدار و نرخ دارد، پس ردیف هم دارد.
        var holder = (await BuildProfileAsync(db, s.YusufId)).Statement!;
        var saleRow = holder.Entries.First(e => e.Kind == PartnershipStatementLineKind.SaleProceedsHeld);
        var sale = await db.SalesTransactions.AsNoTracking().FirstAsync(x => x.Id == saleRow.SourceId);
        Assert.Equal(sale.QuantityMt, saleRow.QuantityMt);
        Assert.Equal(decimal.Round(sale.TotalUsd / sale.QuantityMt, 2, MidpointRounding.AwayFromZero), saleRow.UnitPriceUsd);

        // پرداخت و سهم مفاد سندِ مقداردار ندارند، پس عددی ساخته نمی‌شود.
        foreach (var entry in holder.Entries.Where(e =>
                     e.Kind is PartnershipStatementLineKind.PartnerPurchase
                         or PartnershipStatementLineKind.PartnerExpense
                         or PartnershipStatementLineKind.ProfitShare))
        {
            Assert.Null(entry.QuantityMt);
            Assert.Null(entry.UnitPriceUsd);
        }

        // و نبودِ داده در جدول «—» چاپ می‌شود، نه صفر.
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");
        Assert.Contains("var noValue = \"—\";", view);
        Assert.Contains("@Optional(entry.QuantityMt, \"N3\")", view);
        Assert.Contains("@Optional(entry.UnitPriceUsd, \"N2\")", view);
    }

    [Fact]
    public async Task AllContracts_ShareOneContinuousLedger()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        db.PartnerSettlements.Add(new PartnerSettlement
        {
            SettlementDate = new DateTime(2026, 8, 24),
            FromPartnerId = s.FawadId,
            ToPartnerId = s.YusufId,
            ContractId = null,
            Amount = 5_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 5_000m
        });
        await db.SaveChangesAsync();

        var all = await BuildProfileAsync(db, s.FawadId);

        // هر دو قرارداد و تسویهٔ بدون قرارداد، در همان یک فهرست‌اند.
        Assert.Contains(all.Entries, e => e.ContractId == s.Contract16Id);
        Assert.Contains(all.Entries, e => e.ContractId == s.Contract17Id);
        Assert.Contains(all.Entries, e => e.ContractId is null);

        // مانده تجمعی روی همان یک فهرست بسته می‌شود.
        Assert.Equal(all.Statement!.NetPositionUsd, all.Entries[^1].RunningBalanceUsd);

        // فیلتر قرارداد فقط ردیف‌ها را کم می‌کند؛ مانده و جمع‌ها سرِ جای خود می‌مانند.
        var filtered = await BuildProfileAsync(db, s.FawadId, contractId: s.Contract16Id);
        Assert.All(filtered.Entries, e => Assert.Equal(s.Contract16Id, e.ContractId));
        Assert.True(filtered.Entries.Count < all.Entries.Count);
        Assert.Equal(all.Statement!.NetPositionUsd, filtered.Statement!.NetPositionUsd);
        Assert.Equal(all.Statement!.TotalCreditUsd, filtered.Statement!.TotalCreditUsd);
        Assert.Equal(all.Statement!.TotalDebitUsd, filtered.Statement!.TotalDebitUsd);
    }

    // ————————————————— ۷ و ۸: خواندن صفحه هیچ سندی نمی‌سازد و P&L تکان نمی‌خورد —————————————————

    [Fact]
    public async Task OpeningTheProfile_CreatesNoPaymentExpenseSaleOrLedgerEntry()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var payments = await db.PaymentTransactions.CountAsync();
        var expenses = await db.ExpenseTransactions.CountAsync();
        var sales = await db.SalesTransactions.CountAsync();
        var ledger = await db.LedgerEntries.CountAsync();
        var settlements = await db.PartnerSettlements.CountAsync();

        await BuildProfileAsync(db, s.FawadId);
        await BuildProfileAsync(db, s.YusufId, tab: "ledger");
        await BuildProfileAsync(db, s.FawadId, tab: "ledger", contractId: s.Contract16Id);

        Assert.Equal(payments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(expenses, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(sales, await db.SalesTransactions.CountAsync());
        Assert.Equal(ledger, await db.LedgerEntries.CountAsync());
        Assert.Equal(settlements, await db.PartnerSettlements.CountAsync());
    }

    [Fact]
    public async Task OpeningTheProfile_LeavesContractProfitAndLossUnchanged()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var before = (await BuildPairAsync(db, s)).Contracts.ToDictionary(
            c => c.ContractId,
            c => (c.SalesUsd, c.PurchaseCostUsd, c.OperationalExpenseUsd, c.BookProfitUsd));

        await BuildProfileAsync(db, s.FawadId);
        await BuildProfileAsync(db, s.YusufId);

        var after = (await BuildPairAsync(db, s)).Contracts;
        foreach (var contract in after)
        {
            var expected = before[contract.ContractId];
            Assert.Equal(expected.SalesUsd, contract.SalesUsd);
            Assert.Equal(expected.PurchaseCostUsd, contract.PurchaseCostUsd);
            Assert.Equal(expected.OperationalExpenseUsd, contract.OperationalExpenseUsd);
            Assert.Equal(expected.BookProfitUsd, contract.BookProfitUsd);
        }
    }

    // ————————————————— فیلترهای تب گردش حساب —————————————————

    [Fact]
    public async Task LedgerFilters_ChangeOnlyTheVisibleRows_NotTheBalance()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var all = await BuildProfileAsync(db, s.FawadId, tab: "ledger");
        var oneContract = await BuildProfileAsync(db, s.FawadId, tab: "ledger", contractId: s.Contract16Id);

        Assert.True(oneContract.Entries.Count < all.Entries.Count);
        Assert.All(oneContract.Entries, e => Assert.Equal(s.Contract16Id, e.ContractId));

        // مانده صفحه دست‌نخورده می‌ماند: فیلتر فقط نمایشی است.
        Assert.Equal(all.Statement!.NetPositionUsd, oneContract.Statement!.NetPositionUsd);

        var dated = await BuildProfileAsync(db, s.FawadId, tab: "ledger",
            fromDate: new DateTime(2026, 8, 23));
        Assert.All(dated.Entries, e => Assert.True(e.Date >= new DateTime(2026, 8, 23)));
        Assert.Equal(all.Statement.NetPositionUsd, dated.Statement!.NetPositionUsd);
    }

    // ————————————————— قاعدهٔ بدهکار/بستانکارِ دفترِ حسابدار —————————————————
    //
    // دفترِ کاغذیِ حسابدار: آنچه شریک به حساب گذاشته — پول رسیده، خرید و مصرفِ
    // پرداخت‌شده، سهم مفاد — بدهکار است، و آنچه به شریک رسیده — تسویهٔ پرداخت‌شده به او
    // و عاید فروش نزد او — بستانکار. مانده = بستانکار − بدهکار.

    [Fact]
    public async Task PartnerFundingAndExpense_LandInTheDebitColumn()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        var purchase = statement.Entries.First(e => e.Kind == PartnershipStatementLineKind.PartnerPurchase);
        Assert.Equal(purchase.AmountUsd, purchase.DebitUsd);
        Assert.Equal(0m, purchase.CreditUsd);

        var expense = statement.Entries.First(e =>
            e.Kind is PartnershipStatementLineKind.PartnerExpense or PartnershipStatementLineKind.PartnerFunding);
        Assert.Equal(expense.AmountUsd, expense.DebitUsd);
        Assert.Equal(0m, expense.CreditUsd);
    }

    [Fact]
    public async Task ProfitShare_LandsInTheDebitColumn()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;
        var profit = statement.Entries.First(e => e.Kind == PartnershipStatementLineKind.ProfitShare);

        Assert.True(profit.EffectUsd > 0m);
        Assert.Equal(profit.AmountUsd, profit.DebitUsd);
        Assert.Equal(0m, profit.CreditUsd);
    }

    [Fact]
    public async Task SaleProceedsHeldByThePartner_LandInTheCreditColumn()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;
        var proceeds = statement.Entries.First(e => e.Kind == PartnershipStatementLineKind.SaleProceedsHeld);

        Assert.Equal(proceeds.AmountUsd, proceeds.CreditUsd);
        Assert.Equal(0m, proceeds.DebitUsd);
    }

    [Fact]
    public async Task Settlements_PaidIsDebit_AndReceivedIsCredit()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        db.PartnerSettlements.AddRange(
            new PartnerSettlement
            {
                SettlementDate = new DateTime(2026, 8, 24),
                FromPartnerId = s.YusufId,
                ToPartnerId = s.FawadId,
                ContractId = null,
                Amount = 7_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 7_000m,
                Description = "تسویه پرداخت‌شده"
            },
            new PartnerSettlement
            {
                SettlementDate = new DateTime(2026, 8, 25),
                FromPartnerId = s.FawadId,
                ToPartnerId = s.YusufId,
                ContractId = null,
                Amount = 9_000m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 9_000m,
                Description = "تسویه دریافت‌شده"
            });
        await db.SaveChangesAsync();

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        // شریک پرداخت کرده: پول از او رفته، پس بدهکار است.
        var paid = statement.Entries.First(e => e.SourceType == "Settlement" && e.AmountUsd == 7_000m);
        Assert.Equal(7_000m, paid.DebitUsd);
        Assert.Equal(0m, paid.CreditUsd);

        // شریک دریافت کرده: پول به او رسیده، پس بستانکار است.
        var received = statement.Entries.First(e => e.SourceType == "Settlement" && e.AmountUsd == 9_000m);
        Assert.Equal(9_000m, received.CreditUsd);
        Assert.Equal(0m, received.DebitUsd);
    }

    [Fact]
    public async Task RunningBalance_MovesRowByRow_AsCreditMinusDebit()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        var previous = 0m;
        foreach (var entry in statement.Entries)
        {
            var expected = decimal.Round(
                previous + entry.CreditUsd - entry.DebitUsd, 2, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, entry.AccountantBalanceUsd);
            previous = entry.AccountantBalanceUsd;
        }

        // آخرین مانده = جمع بستانکار − جمع بدهکار.
        Assert.Equal(
            statement.TotalCreditUsd - statement.TotalDebitUsd,
            statement.Entries[^1].AccountantBalanceUsd);
    }

    [Fact]
    public async Task DisplayMapping_DoesNotTouchTheInternalNetPosition()
    {
        await using var db = CreateDb();
        var s = await SeedAsync(db);

        var statement = (await BuildProfileAsync(db, s.YusufId)).Statement!;

        // علامتِ داخلی همان است که بود: جمعِ اثرها، و برابر با مجموع مانده قراردادها.
        Assert.Equal(statement.NetPositionUsd, statement.Entries[^1].RunningBalanceUsd);
        Assert.Equal(
            statement.NetPositionUsd,
            decimal.Round(statement.Contracts.Sum(c => c.NetPositionUsd), 2, MidpointRounding.AwayFromZero));

        // و ماندهٔ نمایشیِ حسابدار دقیقاً قرینهٔ آن است — نه فرمول تازه‌ای.
        Assert.Equal(-statement.NetPositionUsd, statement.AccountantBalanceUsd);
    }

    [Fact]
    public void WebAndExcel_ShareTheSameDebitCreditMapping()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");
        var export = ReadRepoFile("src/PTGOilSystem.Web/Controllers/PartnersController.Export.cs");

        // هر دو همان سه فیلدِ سرویس را می‌خوانند و هیچ‌کدام منطق تازه‌ای نمی‌نویسند.
        foreach (var source in new[] { view, export })
        {
            var credit = source.IndexOf("CreditUsd", StringComparison.Ordinal);
            var debit = source.IndexOf("DebitUsd", StringComparison.Ordinal);
            var balance = source.IndexOf("AccountantBalanceUsd", StringComparison.Ordinal);
            Assert.True(credit >= 0 && debit >= 0 && balance >= 0);
            // ترتیب ستون‌ها یکی است: بستانکار، بدهکار، مانده.
            Assert.True(credit < debit && debit < balance);
        }

        // ستون مانده در هیچ‌کدام از علامتِ داخلی استفاده نمی‌کند.
        Assert.DoesNotContain("entry.RunningBalanceUsd", view);
        Assert.DoesNotContain("entry.RunningBalanceUsd", export);
    }

    [Fact]
    public void OtherPartyStatements_KeepTheirOwnConvention()
    {
        // صورت‌حساب مشتری/تأمین‌کننده/خدمات‌دهنده/صراف مدل مشترک خودشان را دارند و
        // هیچ‌کدام از نگاشتِ شریک استفاده نمی‌کنند، پس این اصلاح آن‌ها را تکان نمی‌دهد.
        var shared = ReadRepoFile("src/PTGOilSystem.Web/Models/Shared/PartyStatementViewModels.cs");
        Assert.Contains("StatementCreditUsd", shared);
        Assert.Contains("StatementDebitUsd", shared);
        Assert.DoesNotContain("PartnerAccountEntry", shared);

        var table = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_PartyStatementTable.cshtml");
        Assert.DoesNotContain("PartnerAccountEntry", table);
        Assert.DoesNotContain("AccountantBalanceUsd", table);
    }

    // ————————————————— helpers —————————————————

    private static async Task<PartnerProfileViewModel> BuildProfileAsync(
        ApplicationDbContext db,
        int partnerId,
        string? tab = null,
        int? contractId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var controller = new PartnersController(
            db,
            new AuditService(db),
            new MasterDataDeleteSafetyService(db),
            new PartnershipStatementService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Details(partnerId, tab, contractId, fromDate, toDate);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<PartnerProfileViewModel>(view.Model);
    }

    private static async Task<PartnershipStatement> BuildPairAsync(ApplicationDbContext db, Scenario s)
    {
        var statement = await new PartnershipStatementService(db).BuildAsync(s.FawadId, s.YusufId);
        Assert.NotNull(statement);
        return statement!;
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static int Count(string value, string token)
        => (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty
                 })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, normalizedPath);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate).Replace("\r\n", "\n").Replace("\r", "\n");
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Repo file not found: {relativePath}");
    }

    private sealed record Scenario(
        int FawadId,
        int YusufId,
        int Contract16Id,
        int Contract17Id);

    /// <summary>
    /// همان دو قرارداد شراکتی واقعی: خرید از بارگیری، مصارف از مصارف قرارداد،
    /// فروش از سند فروش و پرداخت‌ها از روزنامچه — بدون هیچ عدد ساختگیِ مانده.
    /// </summary>
    private static async Task<Scenario> SeedAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "PTG", Name = "PTG" };
        var product = new Product { Code = "MO", Name = "Base Oil" };
        var supplier = new Supplier { Name = "Refinery", IsActive = true };
        var customer = new Customer { Name = "Buyer", IsActive = true };
        var fawad = new Partner { Code = "PAR-F", Name = "گروپ کمپنی های فواد صدیقی", IsActive = true };
        var yusuf = new Partner { Code = "PAR-Y", Name = "شرکت یوسف اسماعیل", IsActive = true };
        var expenseType = new ExpenseType { Code = "OPS", Name = "مصارف قرارداد" };
        db.AddRange(company, product, supplier, customer, fawad, yusuf, expenseType);
        await db.SaveChangesAsync();

        var c16 = NewPartnershipContract(company.Id, product.Id, supplier.Id, "P-016", "500 تن مبلایل شراکتی", 500m);
        var c17 = NewPartnershipContract(company.Id, product.Id, supplier.Id, "P-017", "1318 تن مبلایل شراکتی", 1318.8517m);
        db.Contracts.AddRange(c16, c17);
        await db.SaveChangesAsync();

        c16.SaleProceedsHolderPartnerId = yusuf.Id;
        c17.SaleProceedsHolderPartnerId = fawad.Id;

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = c16.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c16.Id, PartnerId = yusuf.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c17.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = c17.Id, PartnerId = yusuf.Id, SharePercent = 50m });

        db.LoadingRegisters.AddRange(
            NewLoading(c16.Id, product.Id, 500m, 556m),
            NewLoading(c17.Id, product.Id, 612.316m, 455m),
            NewLoading(c17.Id, product.Id, 302.816m, 450m),
            NewLoading(c17.Id, product.Id, 101.496m, 460m),
            NewLoading(c17.Id, product.Id, 199.898m, 457m),
            NewLoading(c17.Id, product.Id, 102.325m, 480m));

        db.ExpenseTransactions.AddRange(
            NewExpense(expenseType.Id, c16.Id, new DateTime(2026, 4, 20), 72_729.99m),
            NewExpense(expenseType.Id, c17.Id, new DateTime(2026, 6, 20), 183_032.39m));

        var sale16 = NewSale(company.Id, product.Id, customer.Id, "GSALE-10-1", 500m, 447_910.9999m, null);
        var sale17 = NewSale(company.Id, product.Id, customer.Id, "GSALE-11-1", 1307.2m, 1_031_871m, c17.Id);
        db.SalesTransactions.AddRange(sale16, sale17);
        await db.SaveChangesAsync();

        // فروش P-016 فقط از راه ردیف لجر به قرارداد وصل است — مثل دادهٔ واقعی.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 8, 22),
            Side = LedgerSide.Credit,
            AmountUsd = 447_910.9999m,
            Currency = "USD",
            ContractId = c16.Id,
            SourceType = "Sale",
            SourceId = sale16.Id,
            Description = "Sale"
        });

        AddFunding(db, c16.Id, fawad.Id, 278_000m, PaymentKind.SupplierPayment, "خرید");
        AddFunding(db, c16.Id, fawad.Id, 18_575m, PaymentKind.ServiceProviderPayment, "مصارف ترکمنستان");
        AddFunding(db, c16.Id, fawad.Id, 25_000m, PaymentKind.ServiceProviderPayment, "کرایه");
        AddFunding(db, c16.Id, yusuf.Id, 27_155m, PaymentKind.ServiceProviderPayment, "گمرک");
        AddFunding(db, c16.Id, yusuf.Id, 2_000m, PaymentKind.ServiceProviderPayment, "شب‌خواب موترها");

        AddFunding(db, c17.Id, yusuf.Id, 278_603.78m, PaymentKind.SupplierPayment, "خرید بچ ۱");
        AddFunding(db, c17.Id, yusuf.Id, 136_267.20m, PaymentKind.SupplierPayment, "خرید بچ ۲");
        AddFunding(db, c17.Id, yusuf.Id, 46_688.16m, PaymentKind.SupplierPayment, "خرید بچ ۳");
        AddFunding(db, c17.Id, yusuf.Id, 91_353.386m, PaymentKind.SupplierPayment, "خرید بچ ۴");
        AddFunding(db, c17.Id, yusuf.Id, 49_116m, PaymentKind.SupplierPayment, "خرید بچ ۵");
        AddFunding(db, c17.Id, yusuf.Id, 35_523.39m, PaymentKind.ServiceProviderPayment, "کرایه تا سرخس");
        AddFunding(db, c17.Id, yusuf.Id, 23_771.98m, PaymentKind.ServiceProviderPayment, "گمرک سرخس");
        AddFunding(db, c17.Id, fawad.Id, 100_200m, PaymentKind.ServiceProviderPayment, "کرایه سرخس تا بخارا");
        AddFunding(db, c17.Id, fawad.Id, 23_539m, PaymentKind.ServiceProviderPayment, "مصارف ازبکستان");

        await db.SaveChangesAsync();

        return new Scenario(fawad.Id, yusuf.Id, c16.Id, c17.Id);
    }

    private static Contract NewPartnershipContract(
        int companyId,
        int productId,
        int supplierId,
        string number,
        string name,
        decimal quantityMt)
        => new()
        {
            ContractNumber = number,
            ContractName = name,
            ContractType = ContractType.Purchase,
            CompanyId = companyId,
            ProductId = productId,
            SupplierId = supplierId,
            OwnershipType = ContractOwnershipType.Partnership,
            Currency = "USD",
            QuantityMt = quantityMt,
            ContractDate = new DateTime(2026, 2, 11)
        };

    private static LoadingRegister NewLoading(int contractId, int productId, decimal quantityMt, decimal priceUsd)
        => new()
        {
            ContractId = contractId,
            ProductId = productId,
            LoadingDate = new DateTime(2026, 5, 10),
            LoadedQuantityMt = quantityMt,
            LoadingPriceUsd = priceUsd
        };

    private static ExpenseTransaction NewExpense(int expenseTypeId, int contractId, DateTime date, decimal amountUsd)
        => new()
        {
            ExpenseTypeId = expenseTypeId,
            ContractId = contractId,
            ExpenseDate = date,
            Amount = amountUsd,
            Currency = "USD",
            AmountUsd = amountUsd,
            Description = "مصارف قرارداد"
        };

    private static SalesTransaction NewSale(
        int companyId,
        int productId,
        int customerId,
        string invoice,
        decimal quantityMt,
        decimal totalUsd,
        int? sourcePurchaseContractId)
        => new()
        {
            CompanyId = companyId,
            ProductId = productId,
            CustomerId = customerId,
            InvoiceNumber = invoice,
            SaleDate = new DateTime(2026, 8, 22),
            QuantityMt = quantityMt,
            UnitPriceUsd = quantityMt == 0m ? 0m : totalUsd / quantityMt,
            TotalUsd = totalUsd,
            Currency = "USD",
            TotalInCurrency = totalUsd,
            AppliedFxRateToUsd = 1m,
            SourcePurchaseContractId = sourcePurchaseContractId
        };

    private static void AddFunding(
        ApplicationDbContext db,
        int contractId,
        int partnerId,
        decimal amountUsd,
        PaymentKind kind,
        string description)
        => db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 8, 23),
            Direction = PaymentDirection.Out,
            PaymentKind = kind,
            ContractId = contractId,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = partnerId,
            Description = description
        });
}
