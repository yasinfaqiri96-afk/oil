using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OperationalAssets;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.Reconciliation;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فاز یک اثر مالی کرایه دارایی عملیاتی.
///
/// جهت Debit/Credit اینجا از منطق واقعی همین سیستم می‌آید و ادعای مستقل نیست:
/// فروش <c>Side = Credit</c> با CustomerId ثبت می‌شود و صورت‌حساب مشتری مانده را با
/// <c>Credit - Debit</c> می‌سازد، پس Credit یعنی مشتری بدهکار شد.
/// </summary>
public class AssetRentPostingTests
{
    // ── ثبت کرایه مشتری ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRent_Customer_Posts_Exactly_One_Credit_Ledger_Linked_To_Rent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.CreateRent(BuildCustomerRent(amount: 5000m));

        Assert.IsType<RedirectToActionResult>(result);

        var ledgers = await db.LedgerEntries.ToListAsync();
        var ledger = Assert.Single(ledgers);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(5000m, ledger.AmountUsd);
        Assert.Equal(1, ledger.CustomerId);
        Assert.Equal(AssetRentLedgerFactory.LedgerSourceType, ledger.SourceType);
        Assert.Null(ledger.ContractId);
        Assert.Null(ledger.ServiceProviderId);

        var rent = await db.AssetRentTransactions.SingleAsync();
        Assert.True(rent.IsPostedToLedger);
        Assert.Equal(ledger.Id, rent.LedgerEntryId);
        Assert.Equal(rent.Id, ledger.SourceId);
    }

    [Fact]
    public async Task CreateRent_Customer_Moves_Customer_RunningBalance_By_Rent_Amount()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        var profile = await LoadCustomerProfileAsync(db, customerId: 1);

        Assert.Equal(5000m, profile.LedgerCreditUsd);
        Assert.Equal(0m, profile.LedgerDebitUsd);
        var row = Assert.Single(profile.StatementRows);
        Assert.Equal("کرایه دارایی", row.Type);
        Assert.Equal(5000m, row.CreditUsd);
        Assert.Null(row.DebitUsd);
        Assert.Equal(5000m, row.RunningBalanceUsd);
    }

    [Fact]
    public async Task CreateRent_Foreign_Currency_Keeps_Source_Amount_And_Rate_On_Ledger()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 2, Code = "AED", Name = "UAE Dirham", Symbol = "AED", IsActive = true });
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 400m);
        model.Currency = "AED";
        model.FxRateToUsd = 0.25m;
        await BuildController(db).CreateRent(model);

        var rent = await db.AssetRentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync();

        Assert.Equal(100m, rent.AmountUsd);
        Assert.Equal(100m, ledger.AmountUsd);
        Assert.Equal("USD", ledger.Currency);
        Assert.Equal(400m, ledger.SourceAmount);
        Assert.Equal("AED", ledger.SourceCurrencyCode);
        Assert.Equal(0.25m, ledger.AppliedFxRateToUsd);
        Assert.Equal(rent.FxRateToUsd, ledger.AppliedFxRateToUsd);
    }

    [Fact]
    public async Task CreateRent_SalesContract_Posts_ContractId_And_Contract_Customer()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        // اعتبارسنجی موجود، کرایهٔ ExternalCustomerRental بدون مشتری/شرکت/شرکت خدماتی را رد می‌کند؛
        // کرایه‌ای که روی خودِ قرارداد فروش می‌نشیند UsageType = Other دارد. این قاعده تغییر نکرده.
        var model = BuildCustomerRent(amount: 900m);
        model.UsageType = AssetRentUsageType.Other;
        model.ChargedToType = AssetRentChargedToType.SalesContract;
        model.ChargedToContractId = 2;
        model.ChargedToCustomerId = null;
        await BuildController(db).CreateRent(model);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(2, ledger.ContractId);
        Assert.Equal(1, ledger.CustomerId);
    }

    [Fact]
    public async Task CreateRent_Internal_Company_Use_Creates_No_Ledger()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 5000m);
        model.UsageType = AssetRentUsageType.InternalCompanyUse;
        model.ChargedToType = AssetRentChargedToType.CompanyInternal;
        model.ChargedToCustomerId = null;
        model.ChargedToCompanyId = 1;
        await BuildController(db).CreateRent(model);

        var rent = await db.AssetRentTransactions.SingleAsync();
        Assert.False(rent.IsPostedToLedger);
        Assert.Null(rent.LedgerEntryId);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Equal(
            AssetRentPostingPolicy.SkipInternalUse,
            AssetRentPostingPolicy.ResolveSkipReason(rent));
    }

    [Fact]
    public async Task CreateRent_Partner_Posts_On_The_Partner_Account()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 700m);
        model.UsageType = AssetRentUsageType.PartnerUse;
        model.ChargedToType = AssetRentChargedToType.Partner;
        model.ChargedToCustomerId = null;
        model.ChargedToPartnerId = 1;
        await BuildController(db).CreateRent(model);

        var rent = await db.AssetRentTransactions.SingleAsync();
        Assert.True(rent.IsPostedToLedger);
        var ledger = Assert.Single(await db.LedgerEntries.ToListAsync());
        Assert.Equal(1, ledger.PartnerId);
        Assert.Null(AssetRentPostingPolicy.ResolveSkipReason(rent));
    }

    // ── جلوگیری از ثبت دوباره ────────────────────────────────────────────────

    [Fact]
    public async Task PostRent_Is_Guarded_Against_Duplicate_Ledger_By_Existing_Entry()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        var rent = await db.AssetRentTransactions.SingleAsync();
        // شبیه‌سازی اجرای دوباره مسیر ثبت روی همان کرایه: پرچم پاک می‌شود ولی دفتر هنوز سطر دارد.
        rent.IsPostedToLedger = false;
        rent.LedgerEntryId = null;
        await db.SaveChangesAsync();

        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        // کرایه دوم سطر خودش را دارد؛ کرایه اول سطر دومی نگرفته است.
        var ledgersForFirstRent = await db.LedgerEntries
            .Where(l => l.SourceType == AssetRentLedgerFactory.LedgerSourceType && l.SourceId == rent.Id)
            .ToListAsync();
        Assert.Single(ledgersForFirstRent);
    }

    // ── لغو و برگشت ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelRent_Adds_Reversal_Ledger_And_Nets_Customer_Balance_To_Zero()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();

        await BuildController(db).CancelRent(rent.Id, reason: "تست لغو");

        var ledgers = await db.LedgerEntries.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, ledgers.Count);
        Assert.Equal(LedgerSide.Credit, ledgers[0].Side);
        Assert.Equal(LedgerSide.Debit, ledgers[1].Side);
        Assert.Equal(ledgers[0].AmountUsd, ledgers[1].AmountUsd);
        Assert.Equal(ledgers[0].SourceId, ledgers[1].SourceId);
        Assert.Equal(AssetRentLedgerFactory.LedgerSourceType, ledgers[1].SourceType);
        Assert.EndsWith(AssetRentLedgerFactory.CancelReferenceSuffix, ledgers[1].Reference);

        var reloaded = await db.AssetRentTransactions.SingleAsync();
        Assert.True(reloaded.IsCancelled);
        Assert.NotNull(reloaded.CancelledAtUtc);
        // رکورد اصلی و لینکش حذف نمی‌شوند.
        Assert.NotNull(reloaded.LedgerEntryId);

        var profile = await LoadCustomerProfileAsync(db, customerId: 1);
        Assert.Equal(0m, profile.LedgerCreditUsd - profile.LedgerDebitUsd);
        Assert.Equal(0m, profile.StatementRows[^1].RunningBalanceUsd);
    }

    [Fact]
    public async Task CancelRent_Twice_Is_Idempotent_And_Adds_No_Third_Row()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();

        await BuildController(db).CancelRent(rent.Id);
        await BuildController(db).CancelRent(rent.Id);

        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    // ── کرایه‌های ساخته‌شده توسط بارگیری ──────────────────────────────────────

    [Fact]
    public void Loading_Generated_Rent_Is_Never_Postable()
    {
        var rent = new AssetRentTransaction
        {
            Id = 10,
            OperationalAssetId = 1,
            LoadingRegisterId = 55,
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            ChargedToCustomerId = 1,
            AmountOriginal = 5000m,
            AmountUsd = 5000m,
            Currency = "USD",
            FxRateToUsd = 1m
        };

        Assert.True(AssetRentPostingPolicy.IsSystemGenerated(rent));
        Assert.False(AssetRentPostingPolicy.ShouldPostToLedger(rent));
        Assert.Equal(
            AssetRentPostingPolicy.SkipSystemGenerated,
            AssetRentPostingPolicy.ResolveSkipReason(rent));
    }

    [Theory]
    [InlineData(nameof(AssetRentTransaction.TransportLegId))]
    [InlineData(nameof(AssetRentTransaction.InventoryTransportReceiptId))]
    [InlineData(nameof(AssetRentTransaction.TruckDispatchId))]
    public void Every_Operational_Link_Marks_Rent_As_System_Generated(string linkName)
    {
        var rent = new AssetRentTransaction
        {
            OperationalAssetId = 1,
            ChargedToType = AssetRentChargedToType.Customer,
            ChargedToCustomerId = 1,
            AmountOriginal = 100m,
            AmountUsd = 100m
        };
        typeof(AssetRentTransaction).GetProperty(linkName)!.SetValue(rent, 7);

        Assert.True(AssetRentPostingPolicy.IsSystemGenerated(rent));
        Assert.False(AssetRentPostingPolicy.ShouldPostToLedger(rent));
    }

    // ── Accounting pilot ─────────────────────────────────────────────────────

    [Fact]
    public async Task Accounting_Adapter_Skips_When_Accounting_Disabled()
    {
        await using var db = CreateDb();
        var adapter = BuildAdapter(db, enabled: false, pilot: false);

        var result = await adapter.TryPostRentAsync(BuildPostableRentEntity());

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("ACCOUNTING_DISABLED", result.Reason);
        Assert.Null(result.Journal);
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task Accounting_Chart_Seeder_Maps_Asset_Accounts_For_New_Company()
    {
        await using var db = CreateDb();
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        await db.SaveChangesAsync();

        await new AccountingChartSeeder(
            db,
            Options.Create(new AccountingOptions { DefaultFunctionalCurrencyCode = "USD" }))
            .SeedAsync();

        var settings = await db.AccountingSettings.SingleAsync();
        var mappedIds = new[]
        {
            settings.FixedAssetAccountId,
            settings.AccumulatedDepreciationAccountId,
            settings.DepreciationExpenseAccountId,
            settings.AssetRentalRevenueAccountId,
            settings.InternalAssetRecoveryAccountId,
            settings.AssetOperatingExpenseAccountId
        };
        Assert.All(mappedIds, id => Assert.True(id.HasValue));
        var mappedCodes = await db.Accounts
            .Where(x => mappedIds.Contains(x.Id))
            .Select(x => x.Code)
            .ToListAsync();
        Assert.Equal(new[] { "1500", "1590", "4300", "4400", "5500", "5600" }, mappedCodes.OrderBy(x => x));
    }

    [Fact]
    public async Task Accounting_Adapter_Skips_When_Pilot_Disabled()
    {
        await using var db = CreateDb();
        var adapter = BuildAdapter(db, enabled: true, pilot: false);

        var result = await adapter.TryPostRentAsync(BuildPostableRentEntity());

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task Accounting_Adapter_Posts_Balanced_Internal_Transfer_With_Asset_Dimension()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        db.Accounts.AddRange(
            new Account { Id = 101, CompanyId = 1, Code = "ASSET-EXP", Name = "Asset operating expense", IsActive = true },
            new Account { Id = 102, CompanyId = 1, Code = "ASSET-REC", Name = "Internal asset recovery", IsActive = true });
        db.AccountingSettings.Add(new AccountingSettings
        {
            CompanyId = 1,
            FunctionalCurrencyCode = "USD",
            AssetOperatingExpenseAccountId = 101,
            InternalAssetRecoveryAccountId = 102
        });
        await db.SaveChangesAsync();
        var posting = new CapturingPostingService();
        var adapter = BuildAdapter(db, enabled: true, pilot: true, posting);
        var rent = BuildPostableRentEntity();
        rent.Id = 99;
        rent.UsageType = AssetRentUsageType.InternalCompanyUse;
        rent.ChargedToType = AssetRentChargedToType.CompanyInternal;
        rent.ChargedToCustomerId = null;
        rent.ChargedToCompanyId = 1;

        var result = await adapter.TryPostRentAsync(rent);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var request = Assert.IsType<AccountingPostRequest>(posting.Request);
        Assert.Equal(2, request.Lines.Count);
        Assert.Equal(5000m, request.Lines.Sum(x => x.Debit));
        Assert.Equal(5000m, request.Lines.Sum(x => x.Credit));
        Assert.All(request.Lines, line => Assert.Equal(1, line.OperationalAssetId));
        Assert.Equal(101, request.Lines.Single(x => x.Debit > 0m).AccountId);
        Assert.Equal(102, request.Lines.Single(x => x.Credit > 0m).AccountId);
    }

    [Fact]
    public async Task Accounting_Adapter_Does_Not_Invent_Internal_Recovery_For_Partner_Owned_Asset()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        var ownership = db.AssetOwnershipShares.Local.Single();
        ownership.OwnerType = AssetOwnerType.Partner;
        ownership.CompanyId = null;
        ownership.PartnerId = 1;
        await db.SaveChangesAsync();
        var rent = BuildPostableRentEntity();
        rent.UsageType = AssetRentUsageType.InternalCompanyUse;
        rent.ChargedToType = AssetRentChargedToType.CompanyInternal;
        rent.ChargedToCustomerId = null;

        var result = await BuildAdapter(db, enabled: true, pilot: true).TryPostRentAsync(rent);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("RENT_COMPANY_UNKNOWN", result.Reason);
    }

    [Fact]
    public async Task Accounting_Adapter_Skips_Loading_Generated_Rent_Even_When_Pilot_Enabled()
    {
        await using var db = CreateDb();
        var adapter = BuildAdapter(db, enabled: true, pilot: true);
        var rent = BuildPostableRentEntity();
        rent.LoadingRegisterId = 42;

        var result = await adapter.TryPostRentAsync(rent);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(AssetRentPostingPolicy.SkipSystemGenerated, result.Reason);
    }

    [Fact]
    public async Task Accounting_Adapter_Reversal_Skips_When_Original_Journal_Missing()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        var adapter = BuildAdapter(db, enabled: true, pilot: true);

        var result = await adapter.TryReverseRentAsync(
            BuildPostableRentEntity(),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("ORIGINAL_JOURNAL_NOT_POSTED", result.Reason);
    }

    // ── Reconciliation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_Flags_Manual_External_Rent_Without_Ledger()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        db.AssetRentTransactions.Add(BuildPostableRentEntity());
        await db.SaveChangesAsync();

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        var issue = Assert.Single(report.AssetRentPostableWithoutLedger);
        Assert.Equal("AssetRentTransaction", issue.SourceType);
        Assert.Equal(5000m, issue.AmountUsd);
    }

    [Fact]
    public async Task Reconciliation_Does_Not_Flag_Internal_Or_Loading_Generated_Rent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);

        var internalRent = BuildPostableRentEntity();
        internalRent.Id = 0;
        internalRent.UsageType = AssetRentUsageType.InternalCompanyUse;
        internalRent.ChargedToType = AssetRentChargedToType.CompanyInternal;
        internalRent.ChargedToCustomerId = null;

        var loadingRent = BuildPostableRentEntity();
        loadingRent.Id = 0;
        loadingRent.LoadingRegisterId = 77;

        db.AssetRentTransactions.AddRange(internalRent, loadingRent);
        await db.SaveChangesAsync();

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        Assert.Empty(report.AssetRentPostableWithoutLedger);
    }

    [Fact]
    public async Task Reconciliation_Does_Not_Flag_Manual_Rent_That_Already_Posted()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        Assert.Empty(report.AssetRentPostableWithoutLedger);
        Assert.Empty(report.AssetRentPostedWithoutLedger);
        Assert.Empty(report.AssetRentLedgerIntegrityIssues);
    }

    [Fact]
    public async Task Reconciliation_Accepts_A_Sales_Contract_Rent_Whose_Ledger_Carries_The_Contract_Customer()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 900m);
        model.UsageType = AssetRentUsageType.Other;
        model.ChargedToType = AssetRentChargedToType.SalesContract;
        model.ChargedToContractId = 2;
        model.ChargedToCustomerId = null;
        await BuildController(db).CreateRent(model);

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        // ردیف عمداً مشتریِ قرارداد را دارد در حالی که خودِ کرایه فقط ContractId دارد؛ این
        // اختلافِ عمدی نباید issue بسازد وگرنه هر کرایهٔ قراردادی false positive می‌شود.
        Assert.Empty(report.AssetRentLedgerIntegrityIssues);
    }

    [Fact]
    public async Task Reconciliation_Flags_Cancelled_Rent_That_Still_Has_An_Unreversed_Ledger()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        // لغو «دستی» بدون برگشت مالی — دقیقاً همان چیزی که هیچ مسیری از کد نباید تولید کند و
        // Reconciliation باید ببیند: کرایه لغو شده ولی مشتری هنوز بدهکار است.
        var rent = await db.AssetRentTransactions.SingleAsync();
        rent.IsCancelled = true;
        rent.CancelledAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        var issue = Assert.Single(report.AssetRentLedgerIntegrityIssues);
        Assert.Contains("no reversal", issue.Issue, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rent.Id, issue.SourceId);
    }

    [Fact]
    public async Task Reconciliation_Does_Not_Flag_A_Cancelled_Rent_That_Was_Properly_Reversed()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();
        await BuildController(db).CancelRent(rent.Id);

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        Assert.Empty(report.AssetRentLedgerIntegrityIssues);
    }

    [Fact]
    public async Task Reconciliation_Flags_A_Second_Original_Ledger_For_The_Same_Rent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();

        // ردیف اصلی دوم روی همان کرایه: همان مبلغ دو بار روی حساب مشتری نشسته است.
        var original = await db.LedgerEntries.SingleAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = original.EntryDate,
            Side = LedgerSide.Credit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceAmount = original.SourceAmount,
            SourceCurrencyCode = original.SourceCurrencyCode,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            Description = original.Description,
            SourceType = AssetRentLedgerFactory.LedgerSourceType,
            SourceId = rent.Id,
            Reference = original.Reference + "-DUP",
            CustomerId = original.CustomerId
        });
        await db.SaveChangesAsync();

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        var issue = Assert.Single(report.AssetRentLedgerIntegrityIssues);
        Assert.Contains("more than one original", issue.Issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconciliation_Flags_A_Ledger_Amount_That_Drifted_From_The_Rent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        var ledger = await db.LedgerEntries.SingleAsync();
        ledger.AmountUsd = 4000m;
        await db.SaveChangesAsync();

        var report = await NewReconciliationService(db).BuildMissingLedgerAsync();

        var issue = Assert.Single(report.AssetRentLedgerIntegrityIssues);
        Assert.Contains("does not match the rent amount", issue.Issue, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4000m, issue.RelatedAmountUsd);
    }

    // ── سرویس مشترک ثبت/برگشت ────────────────────────────────────────────────

    [Fact]
    public async Task ReverseAsync_On_A_Rent_Without_Financial_Posting_Creates_Nothing()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        var loadingRent = BuildPostableRentEntity();
        loadingRent.LoadingRegisterId = 77;
        db.AssetRentTransactions.Add(loadingRent);
        await db.SaveChangesAsync();

        var result = await new AssetRentPostingService(db)
            .ReverseAsync(loadingRent, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.Reversed);
        Assert.Equal(AssetRentPostingService.SkipNoFinancialPosting, result.SkipReason);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task ReverseAsync_Internal_Use_Reverses_Accounting_Without_Legacy_Ledger()
    {
        await using var db = CreateDb();
        var rent = BuildPostableRentEntity();
        rent.UsageType = AssetRentUsageType.InternalCompanyUse;
        rent.LoadingRegisterId = 77;
        db.AssetRentTransactions.Add(rent);
        await db.SaveChangesAsync();
        var accounting = new RecordingAssetRentAccountingAdapter();

        var result = await new AssetRentPostingService(db, accounting)
            .ReverseAsync(rent, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Reversed);
        Assert.Equal(1, accounting.ReverseCalls);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task ReverseAsync_Is_Idempotent_For_A_Posted_Rent()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();
        var service = new AssetRentPostingService(db);
        var reversalDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var first = await service.ReverseAsync(rent, reversalDate);
        var second = await service.ReverseAsync(rent, reversalDate);

        Assert.True(first.Reversed);
        Assert.False(second.Reversed);
        Assert.Equal(AssetRentPostingService.SkipAlreadyReversed, second.SkipReason);
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
        Assert.Equal(
            0m,
            (await db.LedgerEntries.ToListAsync())
                .Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));
    }

    // ── طرف‌حساب‌های دیگر ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRent_ServiceProvider_Posts_On_The_Service_Provider_Account()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.ServiceProviders.Add(new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Id = 1,
            Code = "SP-1",
            Name = "Service Provider A",
            IsActive = true
        });
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 1200m);
        model.ChargedToType = AssetRentChargedToType.Other;
        model.ChargedToCustomerId = null;
        model.ChargedToServiceProviderId = 1;
        await BuildController(db).CreateRent(model);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1200m, ledger.AmountUsd);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Null(ledger.CustomerId);
        Assert.Null(ledger.ContractId);
    }

    [Fact]
    public async Task CreateRent_PurchaseContract_Posts_Contract_Without_A_Customer()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();

        var model = BuildCustomerRent(amount: 700m);
        model.UsageType = AssetRentUsageType.Other;
        model.ChargedToType = AssetRentChargedToType.PurchaseContract;
        model.ChargedToContractId = 3;
        model.ChargedToCustomerId = null;
        await BuildController(db).CreateRent(model);

        var ledger = await db.LedgerEntries.SingleAsync();
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(3, ledger.ContractId);
        // قرارداد خرید مشتری ندارد؛ ردیف نباید مشتری از جای دیگری حدس بزند.
        Assert.Null(ledger.CustomerId);
    }

    // ── سازگاری بین حساب مشتری و درآمد دارایی ────────────────────────────────

    [Fact]
    public async Task One_Rent_Is_Counted_Once_By_The_Customer_And_Once_By_The_Asset()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));

        var customer = await LoadCustomerProfileAsync(db, customerId: 1);
        var asset = await LoadAssetProfileAsync(db, assetId: 1);

        // نه 10000 و نه صفر: یک رویداد اقتصادی، یک بار در حساب مشتری و یک بار در درآمد دارایی.
        Assert.Equal(5000m, customer.LedgerCreditUsd - customer.LedgerDebitUsd);
        Assert.Equal(5000m, asset.ExternalRentUsd);
        Assert.Equal(5000m, asset.TotalRentUsd);
        Assert.Equal(0m, asset.InternalRentUsd);
        Assert.Equal(0m, asset.FreightIncomeUsd);
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Cancelling_A_Rent_Takes_It_Out_Of_Both_The_Customer_Balance_And_Asset_Revenue()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedAssetFullyCompanyOwned(db);
        await db.SaveChangesAsync();
        await BuildController(db).CreateRent(BuildCustomerRent(amount: 5000m));
        var rent = await db.AssetRentTransactions.SingleAsync();

        await BuildController(db).CancelRent(rent.Id);

        var customer = await LoadCustomerProfileAsync(db, customerId: 1);
        var asset = await LoadAssetProfileAsync(db, assetId: 1);

        Assert.Equal(0m, customer.LedgerCreditUsd - customer.LedgerDebitUsd);
        Assert.Equal(0m, asset.TotalRentUsd);
        Assert.Empty(asset.RentTransactions);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AssetRentCreateViewModel BuildCustomerRent(decimal amount)
        => new()
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            ChargedToCustomerId = 1,
            Days = 1m,
            Rate = amount,
            AmountOriginal = amount,
            Currency = "USD",
            FxRateToUsd = 1m,
            ReferenceDocument = null
        };

    private static AssetRentTransaction BuildPostableRentEntity()
        => new()
        {
            OperationalAssetId = 1,
            RentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            UsageType = AssetRentUsageType.ExternalCustomerRental,
            ChargedToType = AssetRentChargedToType.Customer,
            ChargedToCustomerId = 1,
            Rate = 5000m,
            Currency = "USD",
            FxRateToUsd = 1m,
            AmountOriginal = 5000m,
            AmountUsd = 5000m
        };

    private static AssetRentAccountingAdapter BuildAdapter(
        ApplicationDbContext db,
        bool enabled,
        bool pilot,
        IAccountingPostingService? postingService = null)
        => new(
            db,
            postingService ?? new ThrowingPostingService(),
            new AccountingJournalNumberGenerator(),
            Options.Create(new AccountingOptions
            {
                Enabled = enabled,
                Pilots = new AccountingPilotOptions { AssetRent = pilot }
            }),
            NullLogger<AssetRentAccountingAdapter>.Instance);

    private sealed class CapturingPostingService : IAccountingPostingService
    {
        public AccountingPostRequest? Request { get; private set; }

        public Task<JournalEntry> PostAsync(AccountingPostRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new JournalEntry
            {
                Id = 500,
                CompanyId = request.CompanyId,
                JournalNumber = request.JournalNumber,
                SourceModule = request.SourceModule,
                SourceEventId = request.SourceEventId,
                Lines = request.Lines.Select((line, index) => new JournalEntryLine
                {
                    LineNumber = index + 1,
                    AccountId = line.AccountId,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    TransactionCurrencyCode = line.TransactionCurrencyCode,
                    TransactionAmount = line.TransactionAmount,
                    ExchangeRate = line.ExchangeRate,
                    OperationalAssetId = line.OperationalAssetId
                }).ToList()
            });
        }

        public Task<JournalEntry> ReverseAsync(AccountingReversalRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<JournalEntry>> PostBatchAsync(
            IReadOnlyList<AccountingPostRequest> requests,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingAssetRentAccountingAdapter : IAssetRentAccountingAdapter
    {
        public int ReverseCalls { get; private set; }

        public Task<AssetRentAccountingResult> TryPostRentAsync(
            AssetRentTransaction rent,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AssetRentAccountingResult(PaymentPostingStatus.Posted, null, null));

        public Task<AssetRentAccountingResult> TryReverseRentAsync(
            AssetRentTransaction rent,
            DateTime reversalDate,
            CancellationToken cancellationToken = default)
        {
            ReverseCalls++;
            return Task.FromResult(new AssetRentAccountingResult(PaymentPostingStatus.Posted, null, null));
        }
    }

    /// <summary>
    /// ثبت ژورنال واقعی به Chart of Accounts و PostgreSQL نیاز دارد؛ این تست‌ها فقط تضمین می‌کنند
    /// که مسیرهای Skip هرگز به سرویس ثبت نمی‌رسند، پس هر تماس ناخواسته بلافاصله دیده می‌شود.
    /// </summary>
    private sealed class ThrowingPostingService : IAccountingPostingService
    {
        public Task<JournalEntry> PostAsync(AccountingPostRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Posting service must not be reached for a skipped asset rent.");

        public Task<JournalEntry> ReverseAsync(AccountingReversalRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Posting service must not be reached for a skipped asset rent.");

        public Task<IReadOnlyList<JournalEntry>> PostBatchAsync(
            IReadOnlyList<AccountingPostRequest> requests,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Posting service must not be reached for a skipped asset rent.");
    }

    private static ReconciliationService NewReconciliationService(ApplicationDbContext db)
        => new(db, null, new AfghanistanBusinessClock(
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero))));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static async Task<CustomerProfileViewModel> LoadCustomerProfileAsync(
        ApplicationDbContext db,
        int customerId)
    {
        var controller = new CustomersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
        {
            TempData = BuildTempData()
        };
        var result = await controller.Details(customerId);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<CustomerProfileViewModel>(view.Model);
    }

    /// <summary>
    /// پروندهٔ دارایی با بازه‌ای که تاریخ کرایه‌های تست حتماً داخلش باشد، تا نتیجه به «امروز» گره نخورد.
    /// </summary>
    private static async Task<OperationalAssetProfileViewModel> LoadAssetProfileAsync(
        ApplicationDbContext db,
        int assetId)
    {
        var result = await BuildController(db).Details(
            assetId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<OperationalAssetProfileViewModel>(view.Model);
    }

    private static OperationalAssetsController BuildController(ApplicationDbContext db)
        => new(db)
        {
            TempData = BuildTempData(),
            Url = new StubUrlHelper()
        };

    /// <summary>
    /// CancelRent برای ساخت آدرس بازگشت به IUrlHelper نیاز دارد؛ در تست فقط یک مسیر ثابت لازم است.
    /// </summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => "/OperationalAssets/Details/1";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && url.StartsWith('/');
        public string? Link(string? routeName, object? values) => "/OperationalAssets/Details/1";
        public string? RouteUrl(UrlRouteContext routeContext) => "/OperationalAssets/Details/1";
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.Add(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Partners.Add(new Partner { Id = 1, Code = "P-1", Name = "Partner A", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "AFG-101", IsActive = true });
    }

    private static void SeedAssetFullyCompanyOwned(ApplicationDbContext db)
    {
        db.OperationalAssets.Add(new OperationalAsset
        {
            Id = 1,
            AssetCode = "TRK-OWN-1",
            Name = "Owned Truck 1",
            AssetType = OperationalAssetType.Truck,
            LinkedTruckId = 1,
            OwnershipMode = OperationalAssetOwnershipMode.FullyCompanyOwned,
            MonthlyDepreciationUsd = 300m,
            DefaultExternalRateUsd = 5000m,
            IsActive = true
        });
        db.AssetOwnershipShares.Add(new AssetOwnershipShare
        {
            OperationalAssetId = 1,
            OwnerType = AssetOwnerType.Company,
            CompanyId = 1,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    private static TempDataDictionary BuildTempData()
        => new(new DefaultHttpContext(), new NoopTempDataProvider());

    private sealed class NoopTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
