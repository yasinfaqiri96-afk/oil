using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class PartyStatementReadServiceTests
{
    [Theory]
    [InlineData(PartyStatementPartyType.Customer)]
    [InlineData(PartyStatementPartyType.Supplier)]
    [InlineData(PartyStatementPartyType.ServiceProvider)]
    [InlineData(PartyStatementPartyType.Sarraf)]
    [InlineData(PartyStatementPartyType.Employee)]
    [InlineData(PartyStatementPartyType.Partner)]
    [InlineData(PartyStatementPartyType.Driver)]
    [InlineData(PartyStatementPartyType.Company)]
    public void PolicyResolver_DefinesEverySupportedPartyType(PartyStatementPartyType partyType)
    {
        var policy = new PartyStatementPolicyResolver().Resolve(partyType);

        Assert.Equal(partyType, policy.PartyType);
        Assert.False(string.IsNullOrWhiteSpace(policy.StatementTitleFa));
        Assert.False(string.IsNullOrWhiteSpace(policy.ReceiptMeaningFa));
        Assert.False(string.IsNullOrWhiteSpace(policy.OutflowMeaningFa));
        Assert.NotEqual(policy.BalanceMeaning(1m), policy.BalanceMeaning(-1m));
    }

    [Fact]
    public void AllPartyPolicies_ShareOneBalanceMeaning()
    {
        var resolver = new PartyStatementPolicyResolver();

        // قرارداد نمایشی یک‌دست برای همهٔ طرف‌حساب‌ها: ستون برد = آنچه دادیم،
        // ستون رسید = آنچه گرفتیم، بیلانس مثبت = شرکت طلب/پیش‌پرداخت دارد.
        foreach (var partyType in Enum.GetValues<PartyStatementPartyType>())
        {
            var policy = resolver.Resolve(partyType);
            Assert.Contains("طلب", policy.BalanceMeaning(1m));
            Assert.Contains("بدهکار", policy.BalanceMeaning(-1m));
            Assert.Contains("تسویه", policy.BalanceMeaning(0m));
        }
    }

    [Fact]
    public async Task CustomerStatement_CalculatesOpeningTotalsAndRunningBalance_WithBonexFormula()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Atlas Petroleum", Code = "CUST-1" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        db.LedgerEntries.AddRange(
            Entry(new DateTime(2025, 12, 31), LedgerSide.Debit, 10m, customer.Id, "CustomerReceipt", 1),
            Entry(new DateTime(2026, 1, 2), LedgerSide.Credit, 100m, customer.Id, "Sale", 2),
            Entry(new DateTime(2026, 1, 3), LedgerSide.Debit, 40m, customer.Id, "CustomerReceipt", 3));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var statement = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter
            {
                FromDate = new DateTime(2026, 1, 1),
                ToDate = new DateTime(2026, 1, 31),
                IncludeOperationalColumns = false
            });

        Assert.Equal(-10m, statement.Summary.OpeningBalance);
        Assert.Equal(40m, statement.Summary.TotalReceipt);
        Assert.Equal(100m, statement.Summary.TotalOutflow);
        Assert.Equal(50m, statement.Summary.ClosingBalance);
        Assert.Equal(statement.Summary.ClosingBalance, statement.Rows[^1].RunningBalance);
        Assert.True(statement.Rows[0].IsOpeningBalance);
        Assert.Equal("OB", statement.Rows[0].Reference);
    }

    [Fact]
    public async Task BulkBalanceReport_EqualsOfficialPartyStatement_ForOpeningMovementAndClosing()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Shared engine customer", Code = "CUST-SHARED" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            Entry(new DateTime(2025, 12, 31), LedgerSide.Debit, 10m, customer.Id, "CustomerReceipt", 1),
            Entry(new DateTime(2026, 1, 2), LedgerSide.Credit, 100m, customer.Id, "Sale", 2),
            Entry(new DateTime(2026, 1, 3), LedgerSide.Debit, 40m, customer.Id, "CustomerReceipt", 3));
        await db.SaveChangesAsync();

        var filter = new ManagementReportFilterViewModel
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 1, 31),
            CustomerId = customer.Id
        };
        var direction = new CompanyFlowDirectionResolver();
        var balance = new CompanyFlowBalanceService();
        var policies = new PartyStatementPolicyResolver();
        var bulk = new PartyBalanceReadService(db, policies, direction, balance);
        var reportRow = Assert.Single(await bulk.GetBalancesAsync(filter));
        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter
            {
                FromDate = filter.FromDate,
                ToDate = filter.ToDate,
                IncludeOperationalColumns = false
            });

        Assert.Equal(statement.Summary.OpeningBalance, reportRow.OpeningBalanceUsd);
        Assert.Equal(statement.Summary.TotalReceipt, reportRow.TotalReceiptUsd);
        Assert.Equal(statement.Summary.TotalOutflow, reportRow.TotalOutflowUsd);
        Assert.Equal(
            statement.Summary.TotalOutflow - statement.Summary.TotalReceipt,
            reportRow.PeriodMovementUsd);
        Assert.Equal(statement.Summary.ClosingBalance, reportRow.ClosingBalanceUsd);
    }

    [Fact]
    public async Task SupplierStatement_UsesDebitForLoading_AndCreditForPayment()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "BONEX", Code = "SUP-1" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 3, 29),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                Currency = "USD",
                SupplierId = supplier.Id,
                SourceType = "Loading",
                SourceId = 17,
                Description = "MARLIN HPGO"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 3, 30),
                Side = LedgerSide.Debit,
                AmountUsd = 35m,
                Currency = "RUB",
                SourceAmount = 2_800m,
                SourceCurrencyCode = "RUB",
                AppliedFxRateToUsd = 0.0125m,
                SupplierId = supplier.Id,
                SourceType = nameof(PaymentKind.SupplierPayment),
                SourceId = 18,
                Description = "پرداخت"
            });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        // بارگیری (Credit دفتر) در ستون بدهکار «گرفته‌شده» و پرداخت (Debit دفتر) در
        // ستون بستانکار «داده‌شده» می‌نشیند؛ مانده = داده − گرفته = ۳۵ − ۱۰۰.
        Assert.Equal(100m, statement.Summary.TotalReceipt);
        Assert.Equal(35m, statement.Summary.TotalOutflow);
        Assert.Equal(-65m, statement.Summary.ClosingBalance);
        Assert.True(statement.ColumnOptions.ShowRub);
        Assert.True(statement.ColumnOptions.ShowFxRate);
        Assert.Contains(statement.Rows, row => row.FxRateDisplay == "1 USD = 80 RUB");
    }

    [Fact]
    public async Task EmptyAndSingleRowStatements_KeepSummaryAndLastBalanceEqual()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Empty then single" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var service = BuildService(db);

        var empty = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });
        Assert.Empty(empty.Rows);
        Assert.Equal(0m, empty.Summary.ClosingBalance);

        db.LedgerEntries.Add(Entry(new DateTime(2026, 2, 1), LedgerSide.Credit, 25m, customer.Id, "Sale", 9));
        await db.SaveChangesAsync();
        var single = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.Single(single.Rows);
        Assert.Equal(25m, single.Summary.ClosingBalance);
        Assert.Equal(single.Summary.ClosingBalance, single.Rows[^1].RunningBalance);
    }

    [Fact]
    public async Task SameDateRows_AreStableByPostingSequence_AndAdjustmentReversesBalance()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Ordered customer" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var date = new DateTime(2026, 4, 1);
        var later = Entry(date, LedgerSide.Debit, 20m, customer.Id, "Adjustment", 30);
        later.CreatedAtUtc = date.AddHours(2);
        var earlier = Entry(date, LedgerSide.Credit, 100m, customer.Id, "Sale", 20);
        earlier.CreatedAtUtc = date.AddHours(1);
        db.LedgerEntries.AddRange(later, earlier);
        await db.SaveChangesAsync();
        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.Equal(30, statement.Rows[0].SourceId);
        Assert.Equal(-20m, statement.Rows[0].RunningBalance);
        Assert.Equal(80m, statement.Rows[1].RunningBalance);
        Assert.Equal(statement.Summary.ClosingBalance, statement.Rows[^1].RunningBalance);
    }

    [Fact]
    public async Task CurrencyAndCompanyFilters_KeepHistoricalCurrencyAndIsolateCompany()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "Scoped supplier" };
        var firstCompany = new Company { Code = "C1", Name = "Company 1" };
        var secondCompany = new Company { Code = "C2", Name = "Company 2" };
        db.AddRange(supplier, firstCompany, secondCompany);
        await db.SaveChangesAsync();
        var firstContract = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = firstCompany.Id, SupplierId = supplier.Id };
        var secondContract = new Contract { ContractNumber = "P-2", ContractType = ContractType.Purchase, CompanyId = secondCompany.Id, SupplierId = supplier.Id };
        db.AddRange(firstContract, secondContract);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            SupplierEntry(firstContract.Id, supplier.Id, 50m, "RUB", 4_000m, 0.0125m, 1),
            SupplierEntry(secondContract.Id, supplier.Id, 90m, "AED", 330m, null, 2));
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id, firstCompany.Id),
            new PartyStatementFilter { CurrencyCode = "rub", IncludeOperationalColumns = false });

        Assert.Single(statement.Rows);
        // سند بارگیری در ستون بدهکار می‌نشیند، پس مانده منفی (بدهی) است.
        Assert.Equal(-50m, statement.Summary.ClosingBalance);
        Assert.True(statement.ColumnOptions.ShowRub);
        Assert.False(statement.ColumnOptions.ShowAed);
        Assert.Equal("1 USD = 80 RUB", statement.Rows[0].FxRateDisplay);
    }

    [Fact]
    public async Task MissingHistoricalFx_IsNullAndDisplayedAsMissing_NotZero()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "AED supplier" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(SupplierEntry(null, supplier.Id, 10m, "AED", 36.8m, null, 1));
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.True(statement.ColumnOptions.ShowAed);
        Assert.True(statement.ColumnOptions.ShowFxRate);
        Assert.Null(statement.Rows[0].FxRate);
        Assert.Null(statement.Rows[0].FxRateDisplay);
    }

    [Fact]
    public async Task RubPresentation_UsesHistoricalRubOpeningAndPeriodMovement()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "Historical RUB supplier" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = new DateTime(2025, 12, 31),
                Side = LedgerSide.Credit,
                AmountUsd = 100m,
                Currency = "RUB",
                SourceAmount = 8_000m,
                SourceCurrencyCode = "RUB",
                AppliedFxRateToUsd = 0.0125m,
                SupplierId = supplier.Id,
                SourceType = "Loading",
                SourceId = 1,
                Description = "Opening loading"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 1, 2),
                Side = LedgerSide.Debit,
                AmountUsd = 25m,
                Currency = "RUB",
                SourceAmount = 2_000m,
                SourceCurrencyCode = "RUB",
                AppliedFxRateToUsd = 0.0125m,
                SupplierId = supplier.Id,
                SourceType = nameof(PaymentKind.SupplierPayment),
                SourceId = 2,
                Description = "Period payment"
            });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter
            {
                FromDate = new DateTime(2026, 1, 1),
                ToDate = new DateTime(2026, 1, 31),
                CurrencyCode = "RUB",
                IncludeOperationalColumns = false
            });

        Assert.Equal(-8_000m, statement.Summary.OpeningBalanceRub);
        Assert.Equal(0m, statement.Summary.TotalReceiptRub);
        Assert.Equal(2_000m, statement.Summary.TotalOutflowRub);
        Assert.Equal(-6_000m, statement.Summary.ClosingBalanceRub);
        Assert.Equal(-8_000m, statement.Rows[0].RunningBalanceRub);
        Assert.Equal(-6_000m, statement.Rows[^1].RunningBalanceRub);
    }

    [Fact]
    public async Task RubPresentation_MissingHistoricalOriginalAmount_RemainsUnknown()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "Unknown RUB supplier" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2025, 12, 31),
            Side = LedgerSide.Credit,
            AmountUsd = 100m,
            Currency = "RUB",
            SourceAmount = null,
            SourceCurrencyCode = "RUB",
            SupplierId = supplier.Id,
            SourceType = "Loading",
            SourceId = 1,
            Description = "Legacy RUB without original amount"
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter
            {
                FromDate = new DateTime(2026, 1, 1),
                CurrencyCode = "RUB",
                IncludeOperationalColumns = false
            });

        Assert.Null(statement.Summary.OpeningBalanceRub);
        Assert.Null(statement.Summary.ClosingBalanceRub);
        Assert.Null(statement.Rows[0].RunningBalanceRub);
    }

    [Fact]
    public async Task EmployeePolicy_AccrualIncreasesAndPaymentDecreasesPayable()
    {
        await using var db = CreateDb();
        var employee = new Employee { EmployeeCode = "EMP-1", FullName = "Employee One" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryTransactions.AddRange(
            new EmployeeSalaryTransaction { EmployeeId = employee.Id, TransactionDate = new DateTime(2026, 6, 1), TransactionType = EmployeeSalaryTransactionType.SalaryAccrual, Amount = 100m, AmountUsd = 100m, Currency = "USD" },
            new EmployeeSalaryTransaction { EmployeeId = employee.Id, TransactionDate = new DateTime(2026, 6, 2), TransactionType = EmployeeSalaryTransactionType.SalaryPayment, Amount = 40m, AmountUsd = 40m, Currency = "USD" },
            new EmployeeSalaryTransaction { EmployeeId = employee.Id, TransactionDate = new DateTime(2026, 6, 3), TransactionType = EmployeeSalaryTransactionType.Bonus, Amount = 500m, AmountUsd = 500m, Currency = "USD", IsCancelled = true });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Employee, employee.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.Equal(100m, statement.Summary.TotalReceipt);
        Assert.Equal(40m, statement.Summary.TotalOutflow);
        Assert.Equal(-60m, statement.Summary.ClosingBalance);
        Assert.Contains("بدهکار", statement.Summary.ClosingBalanceMeaning);
    }

    [Fact]
    public async Task SarrafStatement_CombinesPostedDirectionsAndViaPayableWithoutDraftRows()
    {
        await using var db = CreateDb();
        var sarraf = new Sarraf { Name = "Exchange House" };
        db.Sarrafs.Add(sarraf);
        await db.SaveChangesAsync();
        db.SarrafSettlements.AddRange(
            new SarrafSettlement { SarrafId = sarraf.Id, SettlementDate = new DateTime(2026, 7, 1), Direction = SarrafSettlementDirection.Out, Status = SarrafSettlementStatus.Posted, SarrafCurrency = "USD", SarrafChargedAmount = 100m, SarrafChargedAmountUsd = 100m },
            new SarrafSettlement { SarrafId = sarraf.Id, SettlementDate = new DateTime(2026, 7, 2), Direction = SarrafSettlementDirection.In, Status = SarrafSettlementStatus.Posted, SarrafCurrency = "USD", SarrafChargedAmount = 20m, SarrafChargedAmountUsd = 20m },
            new SarrafSettlement { SarrafId = sarraf.Id, SettlementDate = new DateTime(2026, 7, 3), Direction = SarrafSettlementDirection.Out, Status = SarrafSettlementStatus.Draft, SarrafCurrency = "USD", SarrafChargedAmount = 999m, SarrafChargedAmountUsd = 999m });
        db.LedgerEntries.Add(new LedgerEntry { EntryDate = new DateTime(2026, 7, 2), Side = LedgerSide.Credit, AmountUsd = 10m, Currency = "USD", SourceType = "SupplierViaSarrafPayable", SourceId = sarraf.Id, Description = "Via sarraf" });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, sarraf.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.Equal(110m, statement.Summary.TotalReceipt);
        Assert.Equal(20m, statement.Summary.TotalOutflow);
        Assert.Equal(-90m, statement.Summary.ClosingBalance);
        Assert.Equal(3, statement.Rows.Count);
    }

    [Fact]
    public async Task PartnerStatement_AppliesContractShareOnce()
    {
        await using var db = CreateDb();
        var partner = new Partner { Code = "PAR-1", Name = "Partner One" };
        var company = new Company { Code = "CO", Name = "Company" };
        db.AddRange(partner, company);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractNumber = "PC-1", ContractType = ContractType.Purchase, CompanyId = company.Id };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.ContractPartners.Add(new ContractPartner { ContractId = contract.Id, PartnerId = partner.Id, SharePercent = 25m });
        db.LedgerEntries.Add(new LedgerEntry { EntryDate = new DateTime(2026, 8, 1), Side = LedgerSide.Credit, AmountUsd = 200m, Currency = "USD", ContractId = contract.Id, SourceType = "Loading", SourceId = 1, Description = "Purchase share" });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Partner, partner.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        // سهم ۲۵٪ از سند ۲۰۰ = ۵۰؛ سند بارگیری (Credit دفتر) در ستون بدهکار می‌نشیند.
        Assert.Equal(50m, statement.Summary.TotalReceipt);
        Assert.Equal(0m, statement.Summary.TotalOutflow);
        Assert.Equal(-50m, statement.Summary.ClosingBalance);
        Assert.Single(statement.Rows);
    }

    [Fact]
    public async Task CsvExport_UsesEveryServiceRowWithoutUiPagination()
    {
        var rows = Enumerable.Range(1, 27)
            .Select(i => new PartyStatementRow
            {
                Sequence = i,
                Date = new DateTime(2026, 1, 1).AddDays(i),
                Reference = $"REF-{i}",
                Description = $"Row {i}",
                OutflowBase = 1m,
                RunningBalance = i,
                SourceType = "Test",
                SourceId = i
            })
            .ToList();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        await using var db = CreateDb();
        var controller = new PartyStatementsController(new StubStatementService(BuildResult(rows)), db)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var action = await controller.Csv(
            PartyStatementPartyType.Customer,
            1,
            new PartyStatementFilter { IncludeOperationalColumns = false });

        await action.ExecuteResultAsync(new ActionContext(httpContext, new(), new()));
        httpContext.Response.Body.Position = 0;
        var csv = await new StreamReader(httpContext.Response.Body, System.Text.Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("REF-27", csv);
        Assert.Equal(28, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void OfficialView_HasRequiredDocumentSections_AndNoFinancialArithmetic()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "PTGOilSystem.Web", "Views", "PartyStatements", "Document.cshtml"));
        var css = File.ReadAllText(Path.Combine(root, "src", "PTGOilSystem.Web", "wwwroot", "css", "ptg", "62-party-statement.css"));

        Assert.Contains("statement-brand-header", view);
        Assert.Contains("statement-info-grid", view);
        Assert.Contains("statement-summary", view);
        Assert.Contains("statement-table", view);
        Assert.Contains("statement-note", view);
        Assert.Contains("statement-authorization", view);
        Assert.Contains("statement-footer", view);
        Assert.DoesNotContain("RunningBalance +=", view, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalOutflow -", view, StringComparison.Ordinal);
        Assert.Contains("@media print", css);
        Assert.Contains("@page statement-wide", css);
    }

    [Fact]
    public async Task SupplierStatement_ExcludesCarrierFreight_ButKeepsSupplierAndLegacyEntries()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "Real supplier" };
        var carrier = new ServiceProvider { Name = "Carrier co" };
        var driver = new Driver { FullName = "Independent driver" };
        db.AddRange(company, supplier, carrier, driver);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        db.Add(contract);
        await db.SaveChangesAsync();

        db.LedgerEntries.AddRange(
            // بدهیِ واقعیِ تأمین‌کننده (SupplierId ست) — باید بماند.
            SupplierEntry(contract.Id, supplier.Id, 1_000m, "USD", 1_000m, 1m, 1),
            // legacy: بدون طرف‌حسابِ دیگر، فقط از طریق قرارداد خرید — باید بماند.
            LegacySupplierEntry(contract.Id, 200m, 2),
            // کرایهٔ حمل با طرف واقعی ServiceProvider — نباید در حساب تأمین‌کننده بیاید.
            FreightEntry(contract.Id, 1_306.30m, 3, serviceProviderId: carrier.Id),
            // کرایهٔ حمل با طرف واقعی Driver — نباید در حساب تأمین‌کننده بیاید.
            FreightEntry(contract.Id, 500m, 4, driverId: driver.Id));
        await db.SaveChangesAsync();

        var ledgerTotalBefore = await db.LedgerEntries.SumAsync(l => l.AmountUsd);

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        // کرایه‌های حمل حذف شده‌اند.
        Assert.DoesNotContain(statement.Rows, r => r.Reference != null && r.Reference.StartsWith("TRANSPORT-RECEIPT", StringComparison.Ordinal));
        // سند واقعی + legacy مانده‌اند (۲ ردیف).
        Assert.Equal(2, statement.Rows.Count(r => !r.IsOpeningBalance));
        // مانده فقط از اسناد واقعیِ تأمین‌کننده: 1000 + 200 = 1200 (کرایهٔ نشتی 1806.30 کنار رفت).
        // هر دو سند بارگیری‌اند و در ستون بدهکار می‌نشینند، پس مانده منفی (بدهی) است.
        Assert.Equal(-1_200m, statement.Summary.ClosingBalance);
        // جمع کل Ledger در دیتابیس تغییر نکرده — فقط انتساب اصلاح شده است.
        Assert.Equal(3_006.30m, ledgerTotalBefore);
        Assert.Equal(ledgerTotalBefore, await db.LedgerEntries.SumAsync(l => l.AmountUsd));
    }

    [Fact]
    public async Task SupplierStatement_ExcludesViaSarrafPayable_EvenWhenItCarriesThePurchaseContract()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "SOLVEX FZE" };
        var sarraf = new Sarraf { Name = "Noorzad Dubai" };
        db.AddRange(company, supplier, sarraf);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        db.Add(contract);
        await db.SaveChangesAsync();

        // همان دو سطری که مسیر تک‌نرخی ViaSarraf می‌سازد.
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = new DateTime(2025, 12, 23),
                Side = LedgerSide.Debit,
                AmountUsd = 2_521_603.8409m,
                Currency = "USD",
                SupplierId = supplier.Id,
                ContractId = contract.Id,
                SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
                SourceId = sarraf.Id,
                Description = "پرداخت از طریق صراف برای تأمین‌کننده"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2025, 12, 23),
                Side = LedgerSide.Credit,
                AmountUsd = 2_521_603.8409m,
                Currency = "USD",
                // بدون SupplierId ولی با ContractId — دقیقاً همان چیزی که پیش‌تر
                // fallbackِ مالکیت را فریب می‌داد.
                ContractId = contract.Id,
                SourceType = PaymentsController.ViaSarrafPayableLedgerSourceType,
                SourceId = sarraf.Id,
                Description = "بدهی شرکت به صراف بابت پرداخت به تأمین‌کننده"
            });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var supplierStatement = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter());
        var sarrafStatement = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, sarraf.Id),
            new PartyStatementFilter());

        // تأمین‌کننده فقط سطر پرداخت را دارد و مانده‌اش صفر نمی‌شود.
        var supplierRow = Assert.Single(supplierStatement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafSupplierLedgerSourceType, supplierRow.SourceType);
        Assert.Equal(2_521_603.8409m, supplierStatement.Summary.ClosingBalance);

        // بدهی صراف سر جایش است.
        var sarrafRow = Assert.Single(sarrafStatement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafPayableLedgerSourceType, sarrafRow.SourceType);
        Assert.Equal(2_521_603.8409m, sarrafRow.ReceiptBase);

        // هیچ سطری از دفتر حذف نشده است.
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task CarrierStatements_StillShowFreightAssignedToThem()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "Real supplier" };
        var carrier = new ServiceProvider { Name = "Carrier co" };
        var driver = new Driver { FullName = "Independent driver" };
        db.AddRange(company, supplier, carrier, driver);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        db.Add(contract);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            FreightEntry(contract.Id, 1_306.30m, 3, serviceProviderId: carrier.Id),
            FreightEntry(contract.Id, 500m, 4, driverId: driver.Id));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var carrierStatement = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.ServiceProvider, carrier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });
        var driverStatement = await service.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Driver, driver.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        Assert.Contains(carrierStatement.Rows, r => r.Reference == "TRANSPORT-RECEIPT:3");
        Assert.DoesNotContain(carrierStatement.Rows, r => r.Reference == "TRANSPORT-RECEIPT:4");
        Assert.Contains(driverStatement.Rows, r => r.Reference == "TRANSPORT-RECEIPT:4");
        Assert.DoesNotContain(driverStatement.Rows, r => r.Reference == "TRANSPORT-RECEIPT:3");
    }

    [Fact]
    public async Task SupplierContractGrouping_PreservesLinearTotals_AndOneRowPerContract()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "Grouped supplier" };
        db.AddRange(company, supplier);
        await db.SaveChangesAsync();
        var c1 = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        var c2 = new Contract { ContractNumber = "P-2", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        db.AddRange(c1, c2);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            SupplierEntry(c1.Id, supplier.Id, 1_000m, "USD", 1_000m, 1m, 1),
            SupplierEntry(c2.Id, supplier.Id, 400m, "USD", 400m, 1m, 2),
            new LedgerEntry { EntryDate = new DateTime(2026, 5, 10), Side = LedgerSide.Debit, AmountUsd = 300m, Currency = "USD", SupplierId = supplier.Id, ContractId = c1.Id, SourceType = "SupplierPayment", SourceId = 5, Description = "payment" },
            // پرداختِ بدون قرارداد — باید در گروهِ «بدون قرارداد» بیاید تا جمع‌ها نشتی نکنند.
            new LedgerEntry { EntryDate = new DateTime(2026, 5, 11), Side = LedgerSide.Debit, AmountUsd = 100m, Currency = "USD", SupplierId = supplier.Id, SourceType = "SupplierPayment", SourceId = 6, Description = "unallocated" });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        var grouping = SupplierContractStatementBuilder.Build(
            statement,
            new Dictionary<int, SupplierContractStatementBuilder.ContractFacts>());

        // یک ردیف برای هر قرارداد + یک ردیف «بدون قرارداد».
        Assert.Equal(3, grouping.Rows.Count);
        Assert.Equal(2, grouping.Rows.Count(r => r.ContractId.HasValue));
        // جمع بدهکار/بستانکار/مانده دقیقاً برابر نمای خطی است (گروه‌بندی فقط نمایشی است).
        Assert.Equal(statement.Summary.TotalReceipt, grouping.Rows.Sum(r => r.Receipt));
        Assert.Equal(statement.Summary.TotalOutflow, grouping.Rows.Sum(r => r.Outflow));
        Assert.Equal(statement.Summary.ClosingBalance, grouping.ClosingBalance);
        Assert.Equal(statement.Summary.ClosingBalance, grouping.Rows[^1].Balance);
    }

    [Fact]
    public async Task SupplierContractGrouping_ExcludesTotalContractValueFromDebit_ForPartiallyLoadedContract()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "BONEX" };
        db.AddRange(company, supplier);
        await db.SaveChangesAsync();
        var contract = new Contract
        {
            ContractNumber = "610",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            SupplierId = supplier.Id,
            QuantityMt = 20_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 600m
        };
        db.Add(contract);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            // بارگیریِ قطعیِ 10,000MT × 600 = 6,000,000 → دفتر Credit → ستون بدهکار.
            new LedgerEntry { EntryDate = new DateTime(2026, 3, 1), Side = LedgerSide.Credit, AmountUsd = 6_000_000m, Currency = "USD", SupplierId = supplier.Id, ContractId = contract.Id, SourceType = "Loading", SourceId = 1, Description = "loading" },
            // پرداخت 4,000,000 → دفتر Debit → ستون بستانکار.
            new LedgerEntry { EntryDate = new DateTime(2026, 3, 2), Side = LedgerSide.Debit, AmountUsd = 4_000_000m, Currency = "USD", SupplierId = supplier.Id, ContractId = contract.Id, SourceType = "SupplierPayment", SourceId = 2, Description = "payment" });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        var price = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
        var facts = new Dictionary<int, SupplierContractStatementBuilder.ContractFacts>
        {
            [contract.Id] = new(
                ProductName: "دیزل",
                ContractQuantityMt: 20_000m,
                UnitPriceUsd: price,
                ContractValueUsd: 20_000m * price!.Value,
                LoadedQuantityMt: 10_000m)
        };
        var grouping = SupplierContractStatementBuilder.Build(statement, facts);

        var row = Assert.Single(grouping.Rows);
        Assert.Equal(6_000_000m, row.Receipt);              // فقط ارزش بارگیریِ قطعی
        Assert.Equal(4_000_000m, row.Outflow);             // پرداخت
        Assert.Equal(-2_000_000m, row.Balance);           // بدهی به تأمین‌کننده
        Assert.Equal(12_000_000m, row.ContractValueUsd);  // ارزش کل قرارداد فقط اطلاعاتی
        Assert.NotEqual(row.ContractValueUsd, row.Receipt);  // ارزش کل قرارداد وارد بدهکار نشده
        Assert.Equal(10_000m, row.RemainingQuantityMt);   // تعهد باقی‌مانده (نه بدهی)
        Assert.Contains("بدهکار", statement.Summary.ClosingBalanceMeaning);
    }

    [Fact]
    public async Task ServiceProviderStatement_MergesOneServiceOnOneShipment_IntoASingleTotalRow()
    {
        await using var db = CreateDb();
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "Supplier" };
        var carrier = new ServiceProvider { Name = "Carrier co" };
        var expenseType = new ExpenseType { Code = "RAIL-FREIGHT", Name = "Rail freight", NamePersian = "کرایه ریلی" };
        db.AddRange(company, supplier, carrier, expenseType);
        await db.SaveChangesAsync();

        var shipment = new Shipment { ShipmentCode = "SHP-1" };
        var c1 = new Contract { ContractNumber = "P-1", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        var c2 = new Contract { ContractNumber = "P-2", ContractType = ContractType.Purchase, CompanyId = company.Id, SupplierId = supplier.Id };
        db.AddRange(shipment, c1, c2);
        await db.SaveChangesAsync();

        // یک خدمت روی یک محموله، هنگام ثبت به دو سهم قراردادی تقسیم شده است.
        var share1 = ShipmentServiceExpense(expenseType.Id, c1.Id, shipment.Id, carrier.Id, 600m);
        var share2 = ShipmentServiceExpense(expenseType.Id, c2.Id, shipment.Id, carrier.Id, 400m);
        db.AddRange(share1, share2);
        await db.SaveChangesAsync();

        db.LedgerEntries.AddRange(
            ShipmentServiceLedger(share1, "RAIL-FREIGHT", "P-1", 60m),
            ShipmentServiceLedger(share2, "RAIL-FREIGHT", "P-2", 40m));
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.ServiceProvider, carrier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(1_000m, row.ReceiptBase);
        Assert.Null(row.ContractId);
        Assert.Equal("RAIL-FREIGHT", row.Reference);
        // جمع‌ها و بیلانس تغییری نمی‌کنند؛ ادغام فقط نمایشی است.
        Assert.Equal(1_000m, statement.Summary.TotalReceipt);
        Assert.Equal(-1_000m, statement.Summary.ClosingBalance);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task StatementRows_DropLedgerTracingTail_FromDescriptionAndReference()
    {
        await using var db = CreateDb();
        var carrier = new ServiceProvider { Name = "Carrier co" };
        db.Add(carrier);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 7, 19),
            Side = LedgerSide.Credit,
            AmountUsd = 500m,
            Currency = "USD",
            ServiceProviderId = carrier.Id,
            SourceType = "Expense",
            SourceId = 91,
            Reference = "RAIL-FREIGHT-91 | مصرف انتقال از موجودی | GroupKey: ITG-1 | Contract: P-1",
            Description = "ثبت هزینه کرایه ریلی - مصرف انتقال | GroupKey: ITG-1 | Total USD: 1,000.0000 | Contract: P-1 | Leg: #4 | Quantity: 300.0000 MT | Share: 60.0000%"
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.ServiceProvider, carrier.Id),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal("RAIL-FREIGHT-91", row.Reference);
        Assert.Equal("ثبت هزینه کرایه ریلی - مصرف انتقال", row.Description);
        Assert.DoesNotContain("GroupKey", row.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Share:", row.Description, StringComparison.Ordinal);
        Assert.True(row.Description.Length <= PartyStatementFormatting.DescriptionMaxLength);
    }

    private static ExpenseTransaction ShipmentServiceExpense(
        int expenseTypeId,
        int contractId,
        int shipmentId,
        int serviceProviderId,
        decimal amountUsd)
        => new()
        {
            ExpenseTypeId = expenseTypeId,
            ContractId = contractId,
            ShipmentId = shipmentId,
            ServiceProviderId = serviceProviderId,
            ExpenseDate = new DateTime(2026, 7, 19),
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd
        };

    private static LedgerEntry ShipmentServiceLedger(
        ExpenseTransaction expense,
        string expenseTypeCode,
        string contractNumber,
        decimal sharePercent)
        => new()
        {
            EntryDate = expense.ExpenseDate,
            Side = LedgerSide.Credit,
            AmountUsd = expense.AmountUsd,
            Currency = "USD",
            SourceAmount = expense.Amount,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            ServiceProviderId = expense.ServiceProviderId,
            SourceType = "Expense",
            SourceId = expense.Id,
            Reference = $"{expenseTypeCode}-{expense.Id} | مصرف انتقال از موجودی",
            Description = $"ثبت هزینه کرایه ریلی - مصرف انتقال از موجودی | GroupKey: ITG-1 | Total USD: 1,000.0000 | Contract: {contractNumber} | Share: {sharePercent:N4}%"
        };

    private static LedgerEntry LegacySupplierEntry(int contractId, decimal amountUsd, int sourceId)
        => new()
        {
            EntryDate = new DateTime(2026, 6, sourceId),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            SourceType = "Loading",
            SourceId = sourceId,
            Description = "Legacy supplier entry"
        };

    private static LedgerEntry FreightEntry(int contractId, decimal amountUsd, int sourceId, int? serviceProviderId = null, int? driverId = null)
        => new()
        {
            EntryDate = new DateTime(2026, 7, 19),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = "USD",
            ContractId = contractId,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId,
            SourceType = "Expense",
            SourceId = sourceId,
            Reference = $"TRANSPORT-RECEIPT:{sourceId}",
            Description = $"Truck receipt freight, receipt #{sourceId}"
        };

    private static LedgerEntry Entry(
        DateTime date,
        LedgerSide side,
        decimal amount,
        int customerId,
        string sourceType,
        int sourceId)
        => new()
        {
            EntryDate = date,
            Side = side,
            AmountUsd = amount,
            Currency = "USD",
            CustomerId = customerId,
            SourceType = sourceType,
            SourceId = sourceId,
            Description = sourceType
        };

    private static LedgerEntry SupplierEntry(
        int? contractId,
        int supplierId,
        decimal amountUsd,
        string currency,
        decimal originalAmount,
        decimal? fxRateToUsd,
        int sourceId)
        => new()
        {
            EntryDate = new DateTime(2026, 5, sourceId),
            Side = LedgerSide.Credit,
            AmountUsd = amountUsd,
            Currency = currency,
            SourceAmount = originalAmount,
            SourceCurrencyCode = currency,
            AppliedFxRateToUsd = fxRateToUsd,
            SupplierId = supplierId,
            ContractId = contractId,
            SourceType = "Loading",
            SourceId = sourceId,
            Description = "Loading"
        };

    private static PartyStatementResult BuildResult(IReadOnlyList<PartyStatementRow> rows)
    {
        var policy = new PartyStatementPolicyResolver().Resolve(PartyStatementPartyType.Customer);
        return new PartyStatementResult
        {
            Party = new PartyRef(PartyStatementPartyType.Customer, 1),
            Policy = policy,
            CompanyInfo = new PartyStatementCompanyInfo(),
            PartyInfo = new PartyStatementPartyInfo { Id = 1, Name = "Customer" },
            DocumentInfo = new PartyStatementDocumentInfo(),
            Summary = new PartyStatementSummary { TotalOutflow = rows.Count, ClosingBalance = rows.Count, BaseCurrencyCode = "USD" },
            ColumnOptions = new PartyStatementColumnOptions(),
            Rows = rows,
            Authorization = new PartyStatementAuthorization()
        };
    }

    private sealed class StubStatementService(PartyStatementResult result) : IPartyStatementReadService
    {
        public Task<PartyStatementResult> GetStatementAsync(
            PartyRef party,
            PartyStatementFilter filter,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private static PartyStatementReadService BuildService(ApplicationDbContext db)
        => new(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            Options.Create(new PartyStatementOptions()));

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
