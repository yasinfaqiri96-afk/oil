using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public interface IAccountingChartSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountingChartSeeder(
    ApplicationDbContext db,
    IOptions<AccountingOptions> options) : IAccountingChartSeeder
{
    // مرحله ۱۳ — MonetaryTreatment فقط برای حساب‌های استانداردی که Seeder مالکِ قطعیِ آن‌هاست
    // تعیین می‌شود. حداقلِ صریحِ پرامپ: Cash/Bank، AR و AP پولی‌اند. Advanceها و بقیهٔ حساب‌ها
    // پیش‌فرض NonMonetary — هیچ حسابی به‌طور ضمنی تسعیر نمی‌شود.
    private static readonly AccountSeed[] DefaultAccounts =
    [
        new("1100", "Cash/Bank Control", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.Monetary),
        new("1200", "Accounts Receivable", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.Monetary),
        new("1300", "Inventory", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1310", "Inventory In Transit", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1400", "Supplier Prepayment", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1410", "Employee Advance", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1500", "Operational Fixed Assets", AccountType.Asset, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("1590", "Accumulated Depreciation", AccountType.Asset, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2100", "Accounts Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.Monetary),
        new("2200", "Customer Advance", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2300", "Freight Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2400", "Commission Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2500", "Employee Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("2510", "Accrued Expenses Payable", AccountType.Liability, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("3100", "Current Year Profit/Loss", AccountType.Equity, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("3200", "Retained Earnings", AccountType.Equity, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4100", "Sales Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4200", "Exchange Gain", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4300", "Asset Rental Revenue", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("4400", "Internal Asset Recovery", AccountType.Revenue, NormalBalance.Credit, MonetaryTreatment.NonMonetary),
        new("5100", "Cost of Goods Sold", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5200", "General Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5300", "Exchange Loss", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5400", "Inventory Loss", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5500", "Depreciation Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary),
        new("5600", "Asset Operating Expense", AccountType.Expense, NormalBalance.Debit, MonetaryTreatment.NonMonetary)
    ];

    private readonly AccountingOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (db.Database.IsRelational())
            DatabaseSafetyGuard.EnsureSeederAllowed(db.Database.GetDbConnection().Database);

        var companyIds = await db.Companies.AsNoTracking()
            .Where(company => !db.AccountingSettings.Any(settings => settings.CompanyId == company.Id))
            .Select(company => company.Id)
            .ToListAsync(cancellationToken);

        foreach (var companyId in companyIds)
            await SeedCompanyAsync(companyId, cancellationToken);
    }

    private async Task SeedCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational())
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (await db.AccountingSettings.AnyAsync(x => x.CompanyId == companyId, cancellationToken))
                return;

            var accountsByCode = await db.Accounts
                .Where(x => x.CompanyId == companyId)
                .ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);

            if (accountsByCode.TryGetValue("2500", out var employeePayable)
                && employeePayable.Name == "Employee/Accrued Payable"
                && !await db.JournalEntryLines.AsNoTracking()
                    .AnyAsync(x => x.AccountId == employeePayable.Id, cancellationToken))
            {
                employeePayable.Name = "Employee Payable";
            }

            foreach (var seed in DefaultAccounts)
            {
                if (accountsByCode.ContainsKey(seed.Code))
                    continue;

                var account = new Account
                {
                    CompanyId = companyId,
                    Code = seed.Code,
                    Name = seed.Name,
                    AccountType = seed.AccountType,
                    NormalBalance = seed.NormalBalance,
                    IsControlAccount = true,
                    AllowManualPosting = false,
                    IsActive = true,
                    MonetaryTreatment = seed.MonetaryTreatment
                };
                db.Accounts.Add(account);
                accountsByCode.Add(seed.Code, account);
            }

            await db.SaveChangesAsync(cancellationToken);

            db.AccountingSettings.Add(new AccountingSettings
            {
                CompanyId = companyId,
                FunctionalCurrencyCode = NormalizeCurrency(_options.DefaultFunctionalCurrencyCode),
                CashBankControlAccountId = accountsByCode["1100"].Id,
                AccountsReceivableAccountId = accountsByCode["1200"].Id,
                InventoryAccountId = accountsByCode["1300"].Id,
                InventoryInTransitAccountId = accountsByCode["1310"].Id,
                SupplierPrepaymentAccountId = accountsByCode["1400"].Id,
                EmployeeAdvanceAccountId = accountsByCode["1410"].Id,
                AccountsPayableAccountId = accountsByCode["2100"].Id,
                CustomerAdvanceAccountId = accountsByCode["2200"].Id,
                FreightPayableAccountId = accountsByCode["2300"].Id,
                CommissionPayableAccountId = accountsByCode["2400"].Id,
                EmployeePayableAccountId = accountsByCode["2500"].Id,
                AccruedExpenseAccountId = accountsByCode["2510"].Id,
                CurrentYearProfitLossAccountId = accountsByCode["3100"].Id,
                RetainedEarningsAccountId = accountsByCode["3200"].Id,
                SalesRevenueAccountId = accountsByCode["4100"].Id,
                ExchangeGainAccountId = accountsByCode["4200"].Id,
                AssetRentalRevenueAccountId = accountsByCode["4300"].Id,
                InternalAssetRecoveryAccountId = accountsByCode["4400"].Id,
                CostOfGoodsSoldAccountId = accountsByCode["5100"].Id,
                GeneralExpenseAccountId = accountsByCode["5200"].Id,
                ExchangeLossAccountId = accountsByCode["5300"].Id,
                InventoryLossAccountId = accountsByCode["5400"].Id,
                DepreciationExpenseAccountId = accountsByCode["5500"].Id,
                AssetOperatingExpenseAccountId = accountsByCode["5600"].Id,
                FixedAssetAccountId = accountsByCode["1500"].Id,
                AccumulatedDepreciationAccountId = accountsByCode["1590"].Id
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static string NormalizeCurrency(string value)
        => string.IsNullOrWhiteSpace(value) ? "USD" : value.Trim().ToUpperInvariant();

    private sealed record AccountSeed(
        string Code,
        string Name,
        AccountType AccountType,
        NormalBalance NormalBalance,
        MonetaryTreatment MonetaryTreatment);
}
