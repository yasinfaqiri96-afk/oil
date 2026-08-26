using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// برگشت audit-preserving برای Ledger قدیمی: ردیف اصلی دست‌نخورده می‌ماند و یک ردیف
/// جبرانیِ هم‌مبلغ با جهت معکوس کنار آن ثبت می‌شود.
/// </summary>
public static class LedgerReversalWriter
{
    // یک تعریف برای نشانهٔ برگشت: نویسندهٔ سطر و خوانندهٔ صورت‌حساب باید دقیقاً یک رشته
    // را بشناسند، وگرنه سطر برگشت مثل سند اصلی خوانده می‌شود و اثر را دو برابر می‌کند.
    public const string CancelReferenceSuffix = CompanyFlow.CompanyFlowSourceTypes.ReversalReferenceSuffix;

    public static async Task<LedgerEntry?> ReverseAsync(
        ApplicationDbContext db,
        LedgerEntry original,
        DateTime reversalDate,
        string description,
        string fallbackReference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(original);

        var reversalReference = (original.Reference ?? fallbackReference) + CancelReferenceSuffix;
        var exists = await db.LedgerEntries
            .AsNoTracking()
            .AnyAsync(l => l.SourceType == original.SourceType
                && l.SourceId == original.SourceId
                && l.Reference == reversalReference,
                ct);
        if (exists)
        {
            return null;
        }

        var reversal = new LedgerEntry
        {
            EntryDate = reversalDate.Date,
            Side = original.Side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceAmount = original.SourceAmount,
            SourceCurrencyCode = original.SourceCurrencyCode,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            AppliedCurrencyPerUsdRate = original.AppliedCurrencyPerUsdRate,
            AppliedFxRateDate = original.AppliedFxRateDate,
            AppliedFxRateSource = original.AppliedFxRateSource,
            Description = description,
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            Reference = reversalReference,
            ViaSarrafGroupId = original.ViaSarrafGroupId,
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            SupplierId = original.SupplierId,
            ServiceProviderId = original.ServiceProviderId,
            DriverId = original.DriverId,
            EmployeeId = original.EmployeeId,
            ShipmentId = original.ShipmentId
        };

        db.LedgerEntries.Add(reversal);
        await db.SaveChangesAsync(ct);
        return reversal;
    }
}
