using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// نگاشت قطعی واژگان «رسید، برد و بیلانس» و تضمین اینکه تغییر زبان فقط متن را عوض
/// می‌کند: مبلغ، جهت، فرمول، علامت و ترتیب ستون‌ها ثابت می‌مانند.
///
///   فارسی : رسید | برد   | بیلانس
///   English: Debit | Credit | Balance
/// </summary>
public sealed class CompanyFlowTextTests
{
    private static HttpContext Context(string language)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"ptg-ui-lang={language}";
        return context;
    }

    // ------------------------------------------------------------------
    // ۱ و ۲ — ستون‌ها در فارسی و انگلیسی
    // ------------------------------------------------------------------

    [Fact]
    public void PersianColumns_AreReceiptOutflowBalance()
    {
        var fa = Context("fa");

        Assert.Equal("رسید", CompanyFlowText.Receipt(fa));
        Assert.Equal("برد", CompanyFlowText.Outflow(fa));
        Assert.Equal("بیلانس", CompanyFlowText.Balance(fa));
    }

    [Fact]
    public void EnglishColumns_AreDebitCreditBalance()
    {
        var en = Context("en");

        Assert.Equal("Debit", CompanyFlowText.Receipt(en));
        Assert.Equal("Credit", CompanyFlowText.Outflow(en));
        Assert.Equal("Balance", CompanyFlowText.Balance(en));
    }

    [Theory]
    [InlineData(CompanyFlowTextKey.TotalReceipt, "مجموع رسید", "Total Debit")]
    [InlineData(CompanyFlowTextKey.TotalOutflow, "مجموع برد", "Total Credit")]
    [InlineData(CompanyFlowTextKey.OpeningBalance, "بیلانس اول دوره", "Opening Balance")]
    [InlineData(CompanyFlowTextKey.RunningBalance, "بیلانس جاری", "Running Balance")]
    [InlineData(CompanyFlowTextKey.ClosingBalance, "بیلانس نهایی", "Closing Balance")]
    [InlineData(CompanyFlowTextKey.Reversal, "سند برگشت", "Reversal Entry")]
    public void EveryCentralKey_HasTheAgreedPersianAndEnglishText(
        CompanyFlowTextKey key,
        string expectedFa,
        string expectedEn)
    {
        Assert.Equal(expectedFa, CompanyFlowText.Get(key, isEnglish: false));
        Assert.Equal(expectedEn, CompanyFlowText.Get(key, isEnglish: true));
    }

    [Fact]
    public void EveryKey_IsDefinedInBothLanguages_AndNamedUnderTheCompanyFlowPrefix()
    {
        foreach (var key in Enum.GetValues<CompanyFlowTextKey>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CompanyFlowText.Get(key, isEnglish: false)));
            Assert.False(string.IsNullOrWhiteSpace(CompanyFlowText.Get(key, isEnglish: true)));
            Assert.StartsWith("CompanyFlow.", CompanyFlowText.KeyName(key), StringComparison.Ordinal);
        }

        Assert.Equal(Enum.GetValues<CompanyFlowTextKey>().Length, CompanyFlowText.Keys.Count);
    }

    [Fact]
    public void ColumnTitlesWithCurrency_KeepTheMappingInBothLanguages()
    {
        Assert.Equal("رسید USD", CompanyFlowText.WithCurrency(CompanyFlowTextKey.Receipt, "USD", isEnglish: false));
        Assert.Equal("Debit USD", CompanyFlowText.WithCurrency(CompanyFlowTextKey.Receipt, "USD", isEnglish: true));
        Assert.Equal("برد (RUB)", CompanyFlowText.WithCurrencyParenthesized(CompanyFlowTextKey.Outflow, "RUB", isEnglish: false));
        Assert.Equal("Credit (RUB)", CompanyFlowText.WithCurrencyParenthesized(CompanyFlowTextKey.Outflow, "RUB", isEnglish: true));
    }

    // ------------------------------------------------------------------
    // ۴ — جهت هیچ ردیفی با زبان عوض نمی‌شود
    // ------------------------------------------------------------------

    [Fact]
    public void SwitchingLanguage_NeverChangesTheDirectionOfARow()
    {
        Assert.Equal("رسید", CompanyFlowText.Label(CompanyFlowDirection.Receipt, isEnglish: false));
        Assert.Equal("Debit", CompanyFlowText.Label(CompanyFlowDirection.Receipt, isEnglish: true));
        Assert.Equal("برد", CompanyFlowText.Label(CompanyFlowDirection.Outflow, isEnglish: false));
        Assert.Equal("Credit", CompanyFlowText.Label(CompanyFlowDirection.Outflow, isEnglish: true));
    }

    // ------------------------------------------------------------------
    // ۳ و ۵ — مبلغ و بیلانس با زبان تغییر نمی‌کنند
    // ------------------------------------------------------------------

    [Fact]
    public void SwitchingLanguage_ChangesNoAmountAndNoBalance()
    {
        var service = new CompanyFlowBalanceService();
        CompanyFlowAmount[] amounts =
        [
            new(CompanyFlowDirection.Receipt, 1_000m),
            new(CompanyFlowDirection.Outflow, 400m)
        ];

        var persianRun = service.Summarize(250m, amounts, CompanyFlowAccountKind.PartyAccount);
        var englishRun = service.Summarize(250m, amounts, CompanyFlowAccountKind.PartyAccount);

        Assert.Equal(persianRun.TotalReceipt, englishRun.TotalReceipt);
        Assert.Equal(persianRun.TotalOutflow, englishRun.TotalOutflow);
        Assert.Equal(persianRun.ClosingBalance, englishRun.ClosingBalance);

        // بیلانس طرف‌حساب = اول دوره + Σبرد − Σرسید — مستقل از زبان.
        Assert.Equal(250m + 400m - 1_000m, persianRun.ClosingBalance);
    }

    [Fact]
    public void CashAccountFormula_StaysOppositeInBothLanguages()
    {
        var service = new CompanyFlowBalanceService();
        CompanyFlowAmount[] amounts =
        [
            new(CompanyFlowDirection.Receipt, 500m),
            new(CompanyFlowDirection.Outflow, 200m)
        ];

        // حساب نقدی = اول دوره + Σرسید − Σبرد. زبان این فرمول را جابه‌جا نمی‌کند.
        var cash = service.Summarize(100m, amounts, CompanyFlowAccountKind.CashAccount);
        Assert.Equal(100m + 500m - 200m, cash.ClosingBalance);

        Assert.Equal("موجودی مثبت حساب", CompanyFlowText.BalanceMeaning(cash.ClosingBalance, CompanyFlowAccountKind.CashAccount, isEnglish: false));
        Assert.Equal("Positive account balance", CompanyFlowText.BalanceMeaning(cash.ClosingBalance, CompanyFlowAccountKind.CashAccount, isEnglish: true));
    }

    [Fact]
    public void BalanceMeaning_KeepsTheSameSignLogicInBothLanguages()
    {
        foreach (var balance in new[] { -1m, 0m, 1m })
        {
            var key = CompanyFlowText.BalanceMeaningKey(balance, CompanyFlowAccountKind.PartyAccount);

            Assert.Equal(CompanyFlowText.Get(key, isEnglish: false), CompanyFlowText.BalanceMeaning(balance, CompanyFlowAccountKind.PartyAccount, isEnglish: false));
            Assert.Equal(CompanyFlowText.Get(key, isEnglish: true), CompanyFlowText.BalanceMeaning(balance, CompanyFlowAccountKind.PartyAccount, isEnglish: true));
        }
    }

    [Fact]
    public void PartyBalanceMeaning_StatesTheCompanyDirectionExplicitly()
    {
        Assert.Equal(
            "شرکت از طرف‌حساب طلبکار است",
            CompanyFlowText.BalanceMeaning(1m, CompanyFlowAccountKind.PartyAccount, isEnglish: false));
        Assert.Equal(
            "شرکت به طرف‌حساب بدهکار است",
            CompanyFlowText.BalanceMeaning(-1m, CompanyFlowAccountKind.PartyAccount, isEnglish: false));
        Assert.Equal(
            "حساب تسویه است",
            CompanyFlowText.BalanceMeaning(0m, CompanyFlowAccountKind.PartyAccount, isEnglish: false));
    }

    [Fact]
    public void EveryPartyPolicy_TranslatesItsColumnMeaningsWithoutFlippingTheSign()
    {
        var resolver = new PartyStatementPolicyResolver();

        foreach (var partyType in Enum.GetValues<PartyStatementPartyType>())
        {
            var policy = resolver.Resolve(partyType);

            Assert.False(string.IsNullOrWhiteSpace(policy.ReceiptMeaning(isEnglish: true)));
            Assert.False(string.IsNullOrWhiteSpace(policy.OutflowMeaning(isEnglish: true)));
            Assert.False(string.IsNullOrWhiteSpace(policy.AccountType(isEnglish: true)));

            // علامت بیلانس در هر دو زبان یک معنی دارد و از منبع مرکزی می‌آید.
            Assert.Equal(
                CompanyFlowText.BalanceMeaning(1m, CompanyFlowAccountKind.PartyAccount, isEnglish: true),
                policy.BalanceMeaning(1m, isEnglish: true));
            Assert.Equal(
                CompanyFlowText.BalanceMeaning(-1m, CompanyFlowAccountKind.PartyAccount, isEnglish: false),
                policy.BalanceMeaning(-1m, isEnglish: false));
        }
    }

    // ------------------------------------------------------------------
    // ۷ — Excel: عنوان ستون‌ها در هر دو زبان درست است و ترتیب ثابت می‌ماند
    // ------------------------------------------------------------------

    [Fact]
    public void ExcelExport_UsesTheCentralTitlesInBothLanguages_AndKeepsColumnOrder()
    {
        var statement = BuildStatement();
        var grouping = BuildGrouping();

        var summary = SupplierStatementExport.BuildSummaryDocument(
            statement, grouping, "stem", "USD", [], isEnglish: false);
        var details = SupplierStatementExport.BuildDetailsDocument(
            statement, "stem", "USD", [], isEnglish: false);

        var flow = details.Columns.TakeLast(3).ToArray();
        Assert.Equal("رسیدگی (USD)", flow[0].TitleFa);
        Assert.Equal("Debit (USD)", flow[0].TitleEn);
        Assert.Equal("بردگی (USD)", flow[1].TitleFa);
        Assert.Equal("Credit (USD)", flow[1].TitleEn);
        Assert.Equal("بیلانس (USD)", flow[2].TitleFa);
        Assert.Equal("Balance (USD)", flow[2].TitleEn);

        Assert.Contains(summary.Columns, c => c.TitleFa == "ارزش قطعی" && c.TitleEn == "Confirmed value");
        Assert.Contains(summary.Columns, c => c.TitleFa == "پرداخت / دریافت" && c.TitleEn == "Payment / receipt");
        Assert.Equal("بیلانس قرارداد", summary.Columns[^1].TitleFa);
        Assert.Equal("Contract balance", summary.Columns[^1].TitleEn);
    }

    [Fact]
    public void ExcelExport_KeepsEveryAmountIdenticalAcrossLanguages()
    {
        var statement = BuildStatement();
        var grouping = BuildGrouping();

        var persian = SupplierStatementExport.BuildSummaryDocument(statement, grouping, "stem", "USD", [], isEnglish: false);
        var english = SupplierStatementExport.BuildSummaryDocument(statement, grouping, "stem", "USD", [], isEnglish: true);

        // فقط ارقام مقایسه می‌شوند؛ زبان عنوان‌ها روی مقدارهای مالی اثر ندارد.
        static object?[] Numbers(TabularExportDocument document) => document.Rows
            .SelectMany(row => row.Cells)
            .Where(cell => cell.ValueType is TabularExportValueType.Number
                or TabularExportValueType.Integer
                or TabularExportValueType.Percentage)
            .Select(cell => cell.Value)
            .ToArray();

        Assert.Equal(Numbers(persian), Numbers(english));
        Assert.Equal(persian.Columns.Count, english.Columns.Count);
    }

    // ------------------------------------------------------------------
    // ۶ — PDF: سند در هر دو زبان ساخته می‌شود و عنوان‌هایش از منبع مرکزی می‌آید
    // ------------------------------------------------------------------

    [Fact]
    public void OpeningBalanceRow_IsTheOnlyTranslatedDescription()
    {
        var opening = new PartyStatementRow
        {
            IsOpeningBalance = true,
            Description = CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish: false)
        };
        var normal = new PartyStatementRow { Description = "پرداخت به تأمین‌کننده #12" };

        Assert.Equal("بیلانس اول دوره", opening.DescriptionFor(isEnglish: false));
        Assert.Equal("Opening Balance", opening.DescriptionFor(isEnglish: true));

        // شرح سند تاریخی ترجمه نمی‌شود؛ داده دست‌نخورده می‌ماند.
        Assert.Equal("پرداخت به تأمین‌کننده #12", normal.DescriptionFor(isEnglish: false));
        Assert.Equal("پرداخت به تأمین‌کننده #12", normal.DescriptionFor(isEnglish: true));
    }

    // ------------------------------------------------------------------
    // ۱۰ — تمام سطح‌های Statement از قرارداد نمایشی واحد استفاده می‌کنند
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("src/PTGOilSystem.Web/Views/PartyStatements/Document.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/PartyStatements/_SupplierContractStatement.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/PartyStatements/_SupplierContractDetails.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/AccountStatements/Contract.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Suppliers/_SupplierStatementByContract.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Suppliers/_SupplierStatementSummaryTable.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/Statements/_PartyStatementRecent.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Views/Shared/Partials/_SupplierStatementLedger.cshtml")]
    [InlineData("src/PTGOilSystem.Web/Controllers/SupplierStatementExport.cs")]
    [InlineData("src/PTGOilSystem.Web/Services/Exports/PartyStatementPdfDocument.cs")]
    public void StatementSurfaces_UseTheOfficialFlowLabels_WithoutBalanceNarratives(string relativePath)
    {
        var text = File.ReadAllText(GetRepoPath(relativePath));

        foreach (var forbidden in new[]
        {
            "ClosingBalanceMeaningFor", "BalanceMeaning(",
            "مانده بدهکار", "مانده بستانکار", "وضعیت مانده"
        })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        Assert.Contains("بیلانس", text, StringComparison.Ordinal);
        if (!relativePath.EndsWith("_SupplierContractStatement.cshtml", StringComparison.Ordinal))
        {
            Assert.Contains("رسیدگی", text, StringComparison.Ordinal);
            Assert.Contains("بردگی", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------
    // داده آزمایشی
    // ------------------------------------------------------------------

    private static PartyStatementResult BuildStatement()
    {
        var policy = new PartyStatementPolicyResolver().Resolve(PartyStatementPartyType.Supplier);

        return new PartyStatementResult
        {
            Party = new PartyRef(PartyStatementPartyType.Supplier, 1),
            Policy = policy,
            CompanyInfo = new PartyStatementCompanyInfo(),
            PartyInfo = new PartyStatementPartyInfo { Id = 1, Name = "Test Supplier" },
            DocumentInfo = new PartyStatementDocumentInfo
            {
                StatementNumber = "ST-1",
                StatementDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
                BaseCurrencyCode = "USD",
                GeneratedAtUtc = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc)
            },
            Summary = new PartyStatementSummary
            {
                OpeningBalance = 250m,
                TotalReceipt = 1_000m,
                TotalOutflow = 400m,
                ClosingBalance = -350m,
                ClosingBalanceMeaning = policy.BalanceMeaning(-350m, isEnglish: false),
                ClosingBalanceMeaningEn = policy.BalanceMeaning(-350m, isEnglish: true),
                BaseCurrencyCode = "USD"
            },
            ColumnOptions = new PartyStatementColumnOptions(),
            Rows =
            [
                new PartyStatementRow
                {
                    Sequence = 0,
                    Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Description = CompanyFlowText.Get(CompanyFlowTextKey.OpeningBalance, isEnglish: false),
                    RunningBalance = 250m,
                    IsOpeningBalance = true
                },
                new PartyStatementRow
                {
                    Sequence = 1,
                    Date = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                    Description = "بارگیری قرارداد ۱",
                    ReceiptBase = 1_000m,
                    RunningBalance = -750m
                },
                new PartyStatementRow
                {
                    Sequence = 2,
                    Date = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc),
                    Description = "پرداخت به تأمین‌کننده",
                    OutflowBase = 400m,
                    RunningBalance = -350m
                }
            ],
            Authorization = new PartyStatementAuthorization()
        };
    }

    private static SupplierContractStatementViewModel BuildGrouping()
        => new()
        {
            Rows =
            [
                new SupplierContractStatementRow
                {
                    Sequence = 1,
                    ContractId = 5,
                    ContractNumber = "C-001",
                    Title = "قرارداد ۱",
                    Receipt = 1_000m,
                    Outflow = 400m,
                    Balance = -600m
                }
            ],
            HasOpeningRow = true,
            OpeningBalance = 250m,
            TotalReceipt = 1_000m,
            TotalOutflow = 400m,
            ClosingBalance = -350m
        };

    private static string GetRepoPath(string relativePath)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
