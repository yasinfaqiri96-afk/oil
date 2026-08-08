using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record AssetRentAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IAssetRentAccountingAdapter
{
    /// <summary>
    /// ژورنالِ درآمدِ یک کرایهٔ دستیِ خارجی را ثبت می‌کند. اگر Accounting یا Pilot خاموش باشد
    /// هیچ ژورنالی ساخته نمی‌شود و ردیفِ لجرِ legacy دست‌نخورده باقی می‌ماند.
    /// </summary>
    Task<AssetRentAccountingResult> TryPostRentAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ژورنالِ کرایهٔ لغوشده را با یک ژورنالِ قرینهٔ متوازن برمی‌گرداند. چیزی حذف نمی‌شود و
    /// فراخوانیِ دوباره بی‌اثر است.
    /// </summary>
    Task<AssetRentAccountingResult> TryReverseRentAsync(
        AssetRentTransaction rent,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 9 dual-write برای کرایهٔ دارایی عملیاتی.
///
///   AssetRent   Dr Accounts Receivable   Cr Sales Revenue    (party = customer)
///
/// دقیقاً همان دو حسابی که <see cref="SalesAccountingAdapter"/> برای درآمدِ فروش استفاده می‌کند.
/// حسابِ درآمدِ اختصاصیِ کرایه عمداً ساخته نشده: افزودنِ آن یعنی ستون جدید در AccountingSettings و
/// Migration، و این فاز صریحاً بدون Migration تعریف شده. تفکیکِ درآمدِ کرایه از درآمدِ فروش از روی
/// <c>SourceModule</c> و <c>SourceEntityType</c> ژورنال قابل انجام است.
///
/// دامنهٔ ثبت را <see cref="AssetRentPostingPolicy"/> تعیین می‌کند — همان کلاسی که Controller و
/// Reconciliation هم از آن می‌پرسند. کرایه‌های خودکارِ بارگیری، استفادهٔ داخلی و کرایهٔ شریک
/// هرگز به اینجا نمی‌رسند.
///
/// شرکتِ مالکِ سند از دادهٔ اثبات‌پذیر می‌آید و حدس زده نمی‌شود: اول قراردادِ طرفِ کرایه، بعد
/// سهم‌های مالکیتِ فعالِ خودِ دارایی اگر همه به یک شرکت برسند. اگر هیچ‌کدام قطعی نبود، ژورنال با
/// دلیلِ روشن رد می‌شود و لجرِ legacy همچنان سرِ جایش است.
/// </summary>
public sealed class AssetRentAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IOptions<AccountingOptions> options,
    ILogger<AssetRentAccountingAdapter> logger)
    : IAssetRentAccountingAdapter
{
    public const string SourceModule = "AssetRent";
    public const string SourceEntityType = nameof(AssetRentTransaction);

    private readonly AccountingOptions _options = options.Value;

    public async Task<AssetRentAccountingResult> TryPostRentAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rent);

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(rent, cancellationToken);
        if (skipReason is not null)
            return Skipped(rent, "AssetRent", companyId, skipReason);

        var sourceEventId = BuildCreatedSourceEventId(rent.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(rent, "AssetRent", companyId, rent.AmountUsd,
                existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new AssetRentAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
        var (partyType, partyId) = await ResolvePartyAsync(rent, cancellationToken);

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForAssetRent(companyId, rent.Id),
            rent.RentDate.Date,
            rent.RentDate.Date,
            rent.RentDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.AccountsReceivableAccountId,
                    Debit: rent.AmountUsd,
                    Credit: 0m,
                    rent.Currency,
                    rent.AmountOriginal,
                    rent.FxRateToUsd,
                    partyType,
                    partyId,
                    ContractId: rent.ChargedToContractId,
                    Description: $"Operational asset rent {AssetRentLedgerFactory.BuildReference(rent)}"),
                new AccountingPostLine(
                    settings.SalesRevenueAccountId,
                    Debit: 0m,
                    Credit: rent.AmountUsd,
                    rent.Currency,
                    rent.AmountOriginal,
                    rent.FxRateToUsd,
                    ContractId: rent.ChargedToContractId,
                    Description: "Operational asset rent revenue")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: rent.Id,
            Description: $"Asset rent #{rent.Id} on {rent.RentDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(rent, "AssetRent", companyId, rent.AmountUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new AssetRentAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(rent, "AssetRent", exception);
            throw;
        }
    }

    public async Task<AssetRentAccountingResult> TryReverseRentAsync(
        AssetRentTransaction rent,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rent);

        if (!_options.Enabled)
            return Skipped(rent, "AssetRentReversal", 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.AssetRent)
            return Skipped(rent, "AssetRentReversal", 0, "PILOT_DISABLED");

        // برخلاف مسیر ثبت، اینجا اعتبارسنجیِ مبلغ/دامنه انجام نمی‌شود: کرایه از قبل لغو شده و آنچه
        // باید برگردد ژورنالی است که واقعاً وجود دارد.
        var companyId = await ResolveCompanyAsync(rent, cancellationToken);
        if (companyId is null)
            return Skipped(rent, "AssetRentReversal", 0, "RENT_COMPANY_UNKNOWN");

        var reversedEventId = BuildReversedSourceEventId(rent.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(rent, "AssetRentReversal", companyId.Value, 0m,
                existingReversal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new AssetRentAccountingResult(
                PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildCreatedSourceEventId(rent.Id), cancellationToken);
        if (original is null)
        {
            // کرایه فقط legacy ثبت شده بود (Pilot خاموش یا Skip)؛ لغوش هم فقط legacy می‌ماند.
            return Skipped(rent, "AssetRentReversal", companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");
        }

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForAssetRentReversal(companyId.Value, rent.Id),
            reversalDate.Date,
            SourceModule,
            reversedEventId,
            Description: $"Reversal of asset rent #{rent.Id}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            LogOutcome(rent, "AssetRentReversal", companyId.Value, rent.AmountUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new AssetRentAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(rent, "AssetRentReversal", exception);
            throw;
        }
    }

    public static string BuildCreatedSourceEventId(int assetRentTransactionId)
        => $"AssetRent:{assetRentTransactionId}:Created";

    public static string BuildReversedSourceEventId(int assetRentTransactionId)
        => $"AssetRent:{assetRentTransactionId}:Reversed";

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return (0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.AssetRent)
            return (0, "PILOT_DISABLED");

        // همان سیاستی که Controller برای ساختِ ردیف لجر به کار می‌برد.
        var policySkip = AssetRentPostingPolicy.ResolveSkipReason(rent);
        if (policySkip is not null)
            return (0, policySkip);

        if (rent.FxRateToUsd <= 0m)
            return (0, "INVALID_RENT_FX");
        if (SystemCurrency.IsBaseCurrency(rent.Currency) && rent.FxRateToUsd != 1m)
            return (0, "INVALID_RENT_FX");

        var expectedUsd = decimal.Round(rent.AmountOriginal * rent.FxRateToUsd, 4, MidpointRounding.AwayFromZero);
        if (rent.AmountUsd != expectedUsd)
            return (0, "INVALID_RENT_CONVERSION");

        var companyId = await ResolveCompanyAsync(rent, cancellationToken);
        if (companyId is null)
            return (0, "RENT_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var accountIds = new[]
        {
            settings.AccountsReceivableAccountId,
            settings.SalesRevenueAccountId
        }.Distinct().ToArray();
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Length)
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    /// <summary>
    /// شرکتِ سندِ کرایه، فقط وقتی اثبات‌پذیر باشد. OperationalAsset خودش ستون CompanyId ندارد، پس
    /// اول قراردادِ طرفِ کرایه و بعد سهم‌های مالکیتِ فعالِ دارایی مبنا قرار می‌گیرند. اگر دارایی بین
    /// چند شرکت مشترک باشد هیچ مالکِ یکتایی وجود ندارد و نتیجه null می‌ماند — همان رفتاری که
    /// SalesAccountingAdapter برای فروشِ بدون شرکتِ یکتا دارد.
    /// </summary>
    private async Task<int?> ResolveCompanyAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken)
    {
        if (rent.ChargedToContractId.HasValue)
        {
            var fromContract = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == rent.ChargedToContractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (fromContract.HasValue)
                return fromContract;
        }

        var ownerCompanies = await db.AssetOwnershipShares
            .AsNoTracking()
            .Where(x => x.OperationalAssetId == rent.OperationalAssetId
                && x.OwnerType == AssetOwnerType.Company
                && x.CompanyId != null
                && x.EffectiveFrom <= rent.RentDate
                && (x.EffectiveTo == null || x.EffectiveTo >= rent.RentDate))
            .Select(x => x.CompanyId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ownerCompanies.Count == 1 ? ownerCompanies[0] : null;
    }

    /// <summary>
    /// طرفِ سطرِ مطالبات. مشتری اولویت دارد؛ اگر کرایه روی قراردادِ فروش نشسته باشد، مشتریِ همان
    /// قرارداد از خودِ قرارداد خوانده می‌شود، نه حدس زده. شرکت خدماتی طرفِ معتبرِ بعدی است.
    /// </summary>
    private async Task<(AccountingPartyType? PartyType, int? PartyId)> ResolvePartyAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken)
    {
        if (rent.ChargedToCustomerId.HasValue)
            return (AccountingPartyType.Customer, rent.ChargedToCustomerId);

        if (rent.ChargedToContractId.HasValue)
        {
            var contractCustomerId = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == rent.ChargedToContractId.Value
                    && x.ContractType == ContractType.Sale
                    && x.CustomerId != null)
                .Select(x => x.CustomerId)
                .SingleOrDefaultAsync(cancellationToken);
            if (contractCustomerId.HasValue)
                return (AccountingPartyType.Customer, contractCustomerId);
        }

        if (rent.ChargedToServiceProviderId.HasValue)
            return (AccountingPartyType.ServiceProvider, rent.ChargedToServiceProviderId);

        return (null, null);
    }

    private async Task<JournalEntry?> FindJournalAsync(
        int companyId,
        string sourceEventId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);

    private AssetRentAccountingResult Skipped(
        AssetRentTransaction rent,
        string eventKind,
        int companyId,
        string reason)
    {
        LogOutcome(rent, eventKind, companyId, 0m, 0m, PaymentPostingStatus.Skipped, reason);
        return new AssetRentAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        AssetRentTransaction rent,
        string eventKind,
        int companyId,
        decimal expectedAmountUsd,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        logger.LogInformation(
            "Asset rent accounting pilot comparison: AssetRentId {AssetRentId}, EventKind {EventKind}, CompanyId {CompanyId}, OperationalAssetId {OperationalAssetId}, CustomerId {CustomerId}, LegacyAmountUsd {LegacyAmountUsd}, ExpectedAmountUsd {ExpectedAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            rent.Id,
            eventKind,
            companyId,
            rent.OperationalAssetId,
            rent.ChargedToCustomerId,
            rent.AmountUsd,
            expectedAmountUsd,
            journalDebitTotal,
            journalDebitTotal - expectedAmountUsd,
            status,
            reason);
    }

    private void LogFailure(AssetRentTransaction rent, string eventKind, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Asset rent accounting pilot posting failed for AssetRentId {AssetRentId} ({EventKind}) with FailureReason {FailureReason}",
            rent.Id,
            eventKind,
            failureReason);
    }
}
