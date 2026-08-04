using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>یک قرارداد مقصد در فورم انتقال.</summary>
public sealed record SupplierBalanceTransferLineRequest(
    int ContractId,
    decimal TransferOriginalAmount,
    decimal ContractCurrencyPerUsdRate);

public sealed record SupplierBalanceTransferCreateRequest(
    int SupplierId,
    // شرکتِ مالکِ مانده. مانده هر شرکتِ اثبات‌شده جداست و انتقالش فقط به قرارداد همان شرکت مجاز است.
    // صفر یعنی «سطح گروه»: شرکت منبع از سند اثبات نشده و شرکتِ هر ردیف از قرارداد مقصدش گرفته می‌شود.
    int CompanyId,
    DateTime TransferDate,
    string CurrencyCode,
    // «۱ دالر = چند واحد ارز مانده؟» (مثلاً ۱۰۰ برای روبل). برای دالر نادیده گرفته می‌شود.
    decimal TransferPerUsdRate,
    IReadOnlyList<SupplierBalanceTransferLineRequest> Lines,
    string? ReferenceNumber,
    string? Notes,
    string? CreatedByUserName);

public sealed record SupplierBalanceTransferReverseRequest(
    int TransferId,
    string ReversalReason,
    string? ReversedByUserName);

public interface ISupplierBalanceTransferService
{
    Task<IReadOnlyList<SupplierBalanceTransfer>> CreateAsync(
        SupplierBalanceTransferCreateRequest request,
        CancellationToken ct = default);

    Task<SupplierBalanceTransfer> ReverseAsync(
        SupplierBalanceTransferReverseRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// موتور واحد «انتقال مانده قابل انتقال تأمین‌کننده به قرارداد».
///
/// این پرداخت جدید نیست: صندوق و بانک اصلاً لمس نمی‌شوند و مانده کلی تأمین‌کننده تغییر نمی‌کند.
/// فقط طلب آزاد به یک یا چند قرارداد خرید همان تأمین‌کننده وصل می‌شود.
///
/// هر ردیف انتقال سه سطر متوازن می‌سازد — دقیقاً همان الگوی SupplierPaymentAllocation:
///   Credit با ContractId = null  → کاهش مانده قابل انتقال به ارزش تاریخی
///   Debit  با ContractId = قرارداد → ثبت ارزش روز انتقال روی قرارداد مقصد
///   سطر سوم فقط وقتی این دو ارزش فرق دارند: سود/زیان نرخ ارز (اثر P&amp;L)
/// بدون سطر سوم، مجموع Debit و Credit برابر نمی‌ماند و مانده تأمین‌کننده جابه‌جا می‌شود.
///
/// انتقال به چند قرارداد در یک BatchId و یک Transaction ثبت می‌شود؛ اگر یک ردیف خطا بدهد،
/// هیچ ردیفی ثبت نمی‌شود.
/// </summary>
public sealed class SupplierBalanceTransferService : ISupplierBalanceTransferService
{
    public const string LedgerSourceType = "SupplierBalanceTransfer";
    public const string ReversalLedgerSourceType = "SupplierBalanceTransferReversal";
    public const string ExchangeDifferenceLedgerSourceType = "SupplierBalanceTransferExchangeDifference";
    public const string ExchangeDifferenceReversalLedgerSourceType = "SupplierBalanceTransferExchangeDifferenceReversal";

    private readonly ApplicationDbContext _db;
    private readonly ISupplierTransferableBalanceService _balances;
    private readonly AccountingOptions? _accountingOptions;

    public SupplierBalanceTransferService(
        ApplicationDbContext db,
        ISupplierTransferableBalanceService? balances = null,
        IOptions<AccountingOptions>? accountingOptions = null)
    {
        _db = db;
        _balances = balances ?? new SupplierTransferableBalanceService(db);
        _accountingOptions = accountingOptions?.Value;
    }

    /// <summary>
    /// نگهبان دفتر کل مرکزی. این موتور هنوز Adapter ژورنال ندارد؛ تا وقتی حسابداری مرکزی و
    /// Pilot این بخش خاموش‌اند مشکلی نیست، ولی اگر روشن شوند نباید سطر Legacy ثبت شود و ژورنال
    /// جا بماند — آن حالت یعنی دو دفتر ناهماهنگ. پس ثبت متوقف می‌شود تا Adapter ساخته شود.
    /// </summary>
    private void GuardCentralAccounting()
    {
        if (_accountingOptions is null || !_accountingOptions.Enabled)
        {
            return;
        }

        if (!_accountingOptions.Pilots.SupplierBalanceTransfer)
        {
            return;
        }

        throw new BusinessRuleException(
            "SUPPLIER_BALANCE_TRANSFER_ACCOUNTING_ADAPTER_MISSING",
            "حسابداری مرکزی برای انتقال مانده فعال شده ولی سند ژورنال آن ساخته نمی‌شود. "
            + "تا ساخته‌شدن Adapter، ثبت انتقال متوقف است تا دو دفتر ناهماهنگ نشوند.");
    }

    public async Task<IReadOnlyList<SupplierBalanceTransfer>> CreateAsync(
        SupplierBalanceTransferCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GuardCentralAccounting();

        var lines = (request.Lines ?? []).Where(l => l.ContractId > 0).ToList();
        if (lines.Count == 0)
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_NO_LINES",
                "حد اقل یک قرارداد مقصد باید انتخاب شود.");
        }

        if (lines.Any(l => l.TransferOriginalAmount <= 0m))
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_AMOUNT_INVALID",
                "مبلغ انتقال باید بزرگ‌تر از صفر باشد.");
        }

        if (lines.Select(l => l.ContractId).Distinct().Count() != lines.Count)
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_DUPLICATE_CONTRACT",
                "یک قرارداد دو بار انتخاب شده است.");
        }

        var currency = SystemCurrency.Normalize(request.CurrencyCode);
        var isUsd = SystemCurrency.IsBaseCurrency(currency);
        if (!isUsd && request.TransferPerUsdRate <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_RATE_INVALID",
                "نرخ روز باید بزرگ‌تر از صفر باشد.");
        }

        var supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
            ?? throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_SUPPLIER_NOT_FOUND",
                "تأمین‌کننده معتبر نیست.");

        var contractIds = lines.Select(l => l.ContractId).ToList();
        var contracts = await _db.Contracts
            .Where(c => contractIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var line in lines)
        {
            if (!contracts.TryGetValue(line.ContractId, out var contract))
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_CONTRACT_NOT_FOUND",
                    "قرارداد مقصد معتبر نیست.");
            }

            if (contract.ContractType != ContractType.Purchase)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_CONTRACT_NOT_PURCHASE",
                    "قرارداد مقصد باید قرارداد خرید باشد.");
            }

            if (contract.SupplierId != supplier.Id)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_SUPPLIER_MISMATCH",
                    "قرارداد مربوط همین تأمین‌کننده نیست.");
            }

            if (!SystemCurrency.IsBaseCurrency(contract.Currency) && line.ContractCurrencyPerUsdRate <= 0m)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_CONTRACT_RATE_INVALID",
                    "نرخ ارز قرارداد مقصد باید بزرگ‌تر از صفر باشد.");
            }
        }

        // دو حالت:
        //   شرکت اثبات‌شده → مانده مال همان شرکت است و قرارداد مقصد هم باید همان شرکت باشد،
        //     وگرنه طلبِ شرکت A به قرارداد شرکت B منتقل می‌شود.
        //   سطح گروه (CompanyId = 0) → شرکتِ منبع از سند اثبات نشده، پس محدودیتی برای مقایسه
        //     وجود ندارد؛ شرکتِ هر ردیف انتقال از قرارداد مقصد همان ردیف گرفته می‌شود و
        //     جفت سطرهای دفترِ همان ردیف در همان شرکت خالصِ صفر می‌مانند.
        var companyId = request.CompanyId;
        var isGroupLevel = companyId == SupplierTransferableBalance.GroupLevelCompanyId;
        if (companyId < 0)
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_COMPANY_REQUIRED",
                "شرکت مانده باید مشخص باشد.");
        }

        if (!isGroupLevel && contracts.Values.Any(c => c.CompanyId != companyId))
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_COMPANY_MISMATCH",
                "قرارداد مقصد مربوط همین شرکت نیست.");
        }
        // نرخ روزِ انتقال همان چیزی است که کاربر وارد کرده و بدون معکوس‌سازی ذخیره می‌شود؛
        // FxRateToUsd فقط ضریبِ محاسباتیِ کنارِ آن است، نه جایگزینش.
        // درخواست فقط نرخ مستقیم را حمل می‌کند؛ نرخ معکوس هرگز از بیرون نمی‌آید و
        // همین‌جا از نرخ مستقیم ساخته می‌شود.
        var transferRates = isUsd ? RatePair.FromPerUsd(1m) : RatePair.FromPerUsd(request.TransferPerUsdRate);
        var transferPerUsd = transferRates.PerUsd!.Value;
        var transferFxRateToUsd = transferRates.FxToUsd;
        var batchId = Guid.NewGuid();
        var transferDate = request.TransferDate.Date;
        var reference = Clean(request.ReferenceNumber, 200);
        var notes = Clean(request.Notes, 1000);
        var createdBy = Clean(request.CreatedByUserName, 150);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            // مانده داخل همین Transaction دوباره خوانده می‌شود تا ثبت هم‌زمان دو کاربر
            // نتواند بیشتر از مانده واقعی مصرف کند.
            var balance = await _balances.GetAsync(supplier.Id, ct);
            var companyBalance = balance.Company(companyId)
                ?? throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_NO_BALANCE",
                    isGroupLevel
                        ? "مانده قابل انتقال سطح گروه وجود ندارد."
                        : "برای این شرکت مانده قابل انتقال وجود ندارد.");
            var bucket = companyBalance.Bucket(currency)
                ?? throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_NO_BALANCE",
                    $"مانده قابل انتقال به {currency} وجود ندارد.");

            var requestedTotal = decimal.Round(lines.Sum(l => l.TransferOriginalAmount), 4, MidpointRounding.AwayFromZero);
            if (requestedTotal > bucket.RemainingOriginalAmount)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_EXCEEDS_BALANCE",
                    $"مانده کافی نیست. مانده فعلی: {bucket.RemainingOriginalAmount:N2} {currency}.");
            }

            var cursor = new FifoCursor(bucket.Slices);
            var created = new List<SupplierBalanceTransfer>(lines.Count);

            foreach (var line in lines)
            {
                var contract = contracts[line.ContractId];
                var originalAmount = decimal.Round(line.TransferOriginalAmount, 4, MidpointRounding.AwayFromZero);
                var consumed = cursor.Take(originalAmount);
                var historicalAmountUsd = decimal.Round(
                    consumed.Sum(c => c.ConsumedBookAmountUsd), 4, MidpointRounding.AwayFromZero);
                if (historicalAmountUsd <= 0m)
                {
                    throw new BusinessRuleException(
                        "SUPPLIER_BALANCE_TRANSFER_EXCEEDS_BALANCE",
                        "مانده کافی نیست.");
                }

                // نرخ تاریخی مستقیم از snapshot خودِ منابع مصرف‌شده می‌آید. اگر همهٔ منابع
                // یک نرخ ثبت‌شدهٔ یکسان داشته باشند، دقیقاً همان عدد قفل می‌شود (۷۰ = ۷۰).
                // اگر نرخ‌ها فرق کنند، خلاصهٔ وزنی از مبالغ واقعی حساب می‌شود — ولی ارزش
                // تاریخیِ هر منبع قبلاً جداگانه در consumed مصرف شده، پس سود/زیان تسعیر
                // از این خلاصه اثر نمی‌گیرد. اگر منبع Legacy باشد، نرخ null و «تخمینی» است.
                var (historicalPerUsd, historicalRateEstimated) =
                    ResolveHistoricalPerUsd(consumed, originalAmount, historicalAmountUsd, isUsd);

                // نرخ معکوس همیشه از نرخ مستقیم ساخته می‌شود تا این دو ستون نتوانند
                // ناسازگار شوند. فقط برای منبع Legacy — که اصلاً نرخ مستقیمی ندارد —
                // از روی مبالغ بازسازی می‌شود، و همان‌جا «تخمینی» علامت خورده است.
                var historicalRates = historicalPerUsd is > 0m
                    ? RatePair.FromPerUsd(historicalPerUsd.Value)
                    : RatePair.Legacy(FxRateMath.RoundRate(historicalAmountUsd / originalAmount));
                var historicalFxRateToUsd = historicalRates.FxToUsd;

                // عمداً با کنوانسیون «مقدار × نرخ» حساب می‌شود (نه تقسیم مستقیم) تا با اعتبارسنجی
                // AccountingPostingService که همین ضرب را دوباره چک می‌کند دقیقاً یکی باشد.
                var transferValueUsd = isUsd
                    ? historicalAmountUsd
                    : decimal.Round(originalAmount * transferFxRateToUsd, 4, MidpointRounding.AwayFromZero);
                if (transferValueUsd <= 0m)
                {
                    throw new BusinessRuleException(
                        "SUPPLIER_BALANCE_TRANSFER_AMOUNT_INVALID",
                        "ارزش دالری انتقال باید بزرگ‌تر از صفر باشد.");
                }

                var differenceUsd = decimal.Round(
                    transferValueUsd - historicalAmountUsd, 4, MidpointRounding.AwayFromZero);
                var differenceType = differenceUsd switch
                {
                    > 0m => SarrafSettlementDifferenceType.Gain,
                    < 0m => SarrafSettlementDifferenceType.Loss,
                    _ => SarrafSettlementDifferenceType.None
                };

                var contractCurrency = SystemCurrency.Normalize(contract.Currency);
                var isContractUsd = SystemCurrency.IsBaseCurrency(contractCurrency);
                var contractRates = RatePair.FromPerUsd(
                    isContractUsd ? 1m : line.ContractCurrencyPerUsdRate);
                var contractPerUsd = contractRates.PerUsd!.Value;
                var contractFxRateToUsd = contractRates.FxToUsd;
                var contractCurrencyAmount = decimal.Round(
                    transferValueUsd * contractPerUsd, 4, MidpointRounding.AwayFromZero);

                var transfer = new SupplierBalanceTransfer
                {
                    BatchId = batchId,
                    SupplierId = supplier.Id,
                    // سطح گروه: شرکت از قرارداد مقصد همین ردیف می‌آید. صفر هرگز ذخیره نمی‌شود.
                    CompanyId = isGroupLevel ? contract.CompanyId : companyId,
                    ContractId = contract.Id,
                    TransferDate = transferDate,
                    TransferOriginalAmount = originalAmount,
                    OriginalCurrencyCode = currency,
                    HistoricalFxRateToUsd = historicalFxRateToUsd,
                    HistoricalCurrencyPerUsdRate = historicalPerUsd,
                    HistoricalRateIsEstimated = historicalRateEstimated,
                    HistoricalAmountUsd = historicalAmountUsd,
                    TransferPerUsdRate = transferPerUsd,
                    TransferFxRateToUsd = transferFxRateToUsd,
                    TransferValueUsd = transferValueUsd,
                    ExchangeDifferenceUsd = differenceUsd,
                    ExchangeDifferenceType = differenceType,
                    ContractCurrencyCode = contractCurrency,
                    ContractCurrencyPerUsdRate = contractPerUsd,
                    ContractCurrencyFxRateToUsd = contractFxRateToUsd,
                    TransferContractCurrencyAmount = contractCurrencyAmount,
                    ReferenceNumber = reference,
                    Notes = notes,
                    Status = SupplierBalanceTransferStatus.Active,
                    CreatedByUserName = createdBy
                };

                foreach (var slice in consumed)
                {
                    transfer.Sources.Add(slice);
                }

                _db.SupplierBalanceTransfers.Add(transfer);
                created.Add(transfer);
            }

            await _db.SaveChangesAsync(ct);

            foreach (var transfer in created)
            {
                var contract = contracts[transfer.ContractId];
                _db.LedgerEntries.AddRange(
                    // ارزش تاریخی از مانده قابل انتقال خارج می‌شود تا باقی‌ماندهٔ موهومی نماند.
                    BuildLedgerEntry(
                        transfer,
                        LedgerSide.Credit,
                        contractId: null,
                        amountUsd: transfer.HistoricalAmountUsd,
                        sourceAmount: transfer.TransferOriginalAmount,
                        sourceCurrency: transfer.OriginalCurrencyCode,
                        appliedFxRateToUsd: transfer.HistoricalFxRateToUsd,
                        appliedPerUsdRate: transfer.HistoricalCurrencyPerUsdRate,
                        sourceType: LedgerSourceType,
                        description: $"کاهش مانده قابل انتقال بابت انتقال به قرارداد {contract.ContractNumber}"),
                    // قرارداد مقصد با ارزش روز انتقال ثبت می‌شود.
                    BuildLedgerEntry(
                        transfer,
                        LedgerSide.Debit,
                        contractId: transfer.ContractId,
                        amountUsd: transfer.TransferValueUsd,
                        sourceAmount: transfer.TransferContractCurrencyAmount,
                        sourceCurrency: transfer.ContractCurrencyCode,
                        appliedFxRateToUsd: transfer.ContractCurrencyFxRateToUsd,
                        appliedPerUsdRate: transfer.ContractCurrencyPerUsdRate,
                        sourceType: LedgerSourceType,
                        description: $"انتقال مانده به قرارداد {contract.ContractNumber} با نرخ روز"));
            }

            await _db.SaveChangesAsync(ct);

            foreach (var transfer in created.Where(t => t.ExchangeDifferenceType != SarrafSettlementDifferenceType.None))
            {
                var differenceLedger = BuildExchangeDifferenceLedger(
                    transfer,
                    contracts[transfer.ContractId],
                    ExchangeDifferenceLedgerSourceType);
                _db.LedgerEntries.Add(differenceLedger);
                await _db.SaveChangesAsync(ct);

                transfer.ExchangeDifferenceLedgerEntryId = differenceLedger.Id;
            }

            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return created;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
    }

    public async Task<SupplierBalanceTransfer> ReverseAsync(
        SupplierBalanceTransferReverseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ReversalReason))
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_REVERSAL_REASON_REQUIRED",
                "دلیل برگشت انتقال ضروری است.");
        }

        var transfer = await _db.SupplierBalanceTransfers
            .Include(t => t.Contract)
            .FirstOrDefaultAsync(t => t.Id == request.TransferId, ct)
            ?? throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_NOT_FOUND",
                "انتقال انتخاب‌شده معتبر نیست.");

        if (transfer.Status != SupplierBalanceTransferStatus.Active)
        {
            throw new BusinessRuleException(
                "SUPPLIER_BALANCE_TRANSFER_ALREADY_REVERSED",
                "این انتقال قبلاً برگشت داده شده است.");
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            transfer.Status = SupplierBalanceTransferStatus.Reversed;
            transfer.ReversedAtUtc = DateTime.UtcNow;
            transfer.ReversedByUserName = Clean(request.ReversedByUserName, 150);
            transfer.ReversalReason = Clean(request.ReversalReason, 500);

            // ثبت‌های معکوس و جداگانه؛ سطرهای اصلی حذف یا ویرایش نمی‌شوند.
            _db.LedgerEntries.AddRange(
                BuildLedgerEntry(
                    transfer,
                    LedgerSide.Debit,
                    contractId: null,
                    amountUsd: transfer.HistoricalAmountUsd,
                    sourceAmount: transfer.TransferOriginalAmount,
                    sourceCurrency: transfer.OriginalCurrencyCode,
                    appliedFxRateToUsd: transfer.HistoricalFxRateToUsd,
                    appliedPerUsdRate: transfer.HistoricalCurrencyPerUsdRate,
                    sourceType: ReversalLedgerSourceType,
                    description: $"برگشت انتقال — بازگشت به مانده قابل انتقال (#{transfer.Id})"),
                BuildLedgerEntry(
                    transfer,
                    LedgerSide.Credit,
                    contractId: transfer.ContractId,
                    amountUsd: transfer.TransferValueUsd,
                    sourceAmount: transfer.TransferContractCurrencyAmount,
                    sourceCurrency: transfer.ContractCurrencyCode,
                    appliedFxRateToUsd: transfer.ContractCurrencyFxRateToUsd,
                    appliedPerUsdRate: transfer.ContractCurrencyPerUsdRate,
                    sourceType: ReversalLedgerSourceType,
                    description: $"برگشت انتقال مانده از قرارداد (#{transfer.Id})"));

            // سود/زیان نرخ ارز هم باید معکوس شود، وگرنه ثبت‌های برگشت نامتوازن می‌مانند.
            if (transfer.ExchangeDifferenceType != SarrafSettlementDifferenceType.None)
            {
                _db.LedgerEntries.Add(BuildExchangeDifferenceLedger(
                    transfer,
                    transfer.Contract,
                    ExchangeDifferenceReversalLedgerSourceType,
                    reverse: true));
            }

            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return transfer;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
    }

    /// <summary>
    /// سطر سود/زیان نرخ ارز — همان الگوی تسویهٔ صراف و تخصیص پیش‌پرداخت:
    /// زیان = Debit، سود = Credit؛ در حالت برگشت سمت آن معکوس می‌شود.
    /// </summary>
    private static LedgerEntry BuildExchangeDifferenceLedger(
        SupplierBalanceTransfer transfer,
        Contract? contract,
        string sourceType,
        bool reverse = false)
    {
        var isLoss = transfer.ExchangeDifferenceType == SarrafSettlementDifferenceType.Loss;
        var amount = Math.Abs(transfer.ExchangeDifferenceUsd);
        var side = isLoss ? LedgerSide.Debit : LedgerSide.Credit;
        if (reverse)
        {
            side = side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit;
        }

        var label = isLoss ? "زیان نرخ ارز انتقال مانده" : "سود نرخ ارز انتقال مانده";
        var contractNumber = contract?.ContractNumber;
        var description = reverse
            ? $"برگشت {label} (#{transfer.Id})"
            : string.IsNullOrWhiteSpace(contractNumber)
                ? $"{label} (#{transfer.Id})"
                : $"{label} بابت قرارداد {contractNumber}";

        return new LedgerEntry
        {
            EntryDate = transfer.TransferDate.Date,
            Side = side,
            AmountUsd = amount,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amount,
            SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = transfer.TransferDate.Date,
            AppliedFxRateSource = sourceType,
            Description = description,
            SourceType = sourceType,
            SourceId = transfer.Id,
            Reference = transfer.ReferenceNumber,
            ContractId = transfer.ContractId
        };
    }

    private static LedgerEntry BuildLedgerEntry(
        SupplierBalanceTransfer transfer,
        LedgerSide side,
        int? contractId,
        decimal amountUsd,
        decimal sourceAmount,
        string sourceCurrency,
        decimal appliedFxRateToUsd,
        decimal? appliedPerUsdRate,
        string sourceType,
        string description)
        => new()
        {
            EntryDate = transfer.TransferDate.Date,
            Side = side,
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = sourceAmount,
            SourceCurrencyCode = sourceCurrency,
            AppliedFxRateToUsd = appliedFxRateToUsd,
            // نرخ مستقیم روی خودِ سطر دفتر می‌نشیند تا خواندنِ بعدی مجبور به بازسازی نشود.
            AppliedCurrencyPerUsdRate = appliedPerUsdRate,
            AppliedFxRateDate = transfer.TransferDate.Date,
            AppliedFxRateSource = sourceType,
            Description = description,
            SourceType = sourceType,
            SourceId = transfer.Id,
            Reference = transfer.ReferenceNumber,
            SupplierId = transfer.SupplierId,
            ContractId = contractId
        };

    /// <summary>
    /// یک جفتِ همیشه‌سازگارِ نرخ.
    ///
    /// هر نرخ در این موتور دو نمایش دارد: مستقیم («۱ دالر = ۷۰») و معکوسِ محاسباتی
    /// (AmountUsd = AmountOriginal × FxToUsd). اگر این دو جدا از هم مقداردهی شوند
    /// می‌توانند ناسازگار ذخیره شوند — مثلاً PerUsd = ۷۰ کنار FxToUsd = 0.014286 که
    /// معکوسش ۶۹.۹۹۸۶ است. این record تنها راهِ ساختِ FxToUsd است و آن را همیشه از
    /// نرخ مستقیم می‌سازد، پس ناسازگاری از نظر ساختاری ممکن نیست.
    ///
    /// <see cref="Legacy"/> تنها استثناست: منبع قدیمی اصلاً نرخ مستقیم ندارد، پس
    /// PerUsd آن null می‌ماند و هیچ عددی به‌عنوان نرخ قطعی جا زده نمی‌شود.
    /// </summary>
    private readonly record struct RatePair(decimal? PerUsd, decimal FxToUsd)
    {
        public static RatePair FromPerUsd(decimal perUsdRate)
            => new(perUsdRate, FxRateMath.ToUsdFromPerUsd(perUsdRate));

        public static RatePair Legacy(decimal fxRateToUsd) => new(null, fxRateToUsd);
    }

    /// <summary>
    /// نرخ تاریخی مستقیم برای یک ردیف انتقال، از snapshot منابعِ مصرف‌شده.
    /// همان ترتیبِ ResolvePerUsdRate در موتور مانده — عمداً یکسان تا کارت و سند یک عدد بدهند.
    /// </summary>
    private static (decimal? PerUsdRate, bool IsEstimated) ResolveHistoricalPerUsd(
        IReadOnlyList<SupplierBalanceTransferSource> consumed,
        decimal originalAmount,
        decimal historicalAmountUsd,
        bool isUsd)
    {
        if (isUsd)
        {
            return (1m, false);
        }

        if (consumed.Count == 0 || originalAmount <= 0m || historicalAmountUsd <= 0m)
        {
            return (null, true);
        }

        if (consumed.Any(c => c.HistoricalCurrencyPerUsdRate is not > 0m))
        {
            // منبع Legacy: نرخ بازسازی می‌شود ولی به‌عنوان نرخ قطعی ذخیره نمی‌شود.
            return (null, true);
        }

        var distinct = consumed
            .Select(c => c.HistoricalCurrencyPerUsdRate!.Value)
            .Distinct()
            .ToList();

        return distinct.Count == 1
            ? (distinct[0], false)
            : (FxRateMath.RoundRate(originalAmount / historicalAmountUsd), false);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>
    /// مصرف FIFO از برش‌های مانده: قدیمی‌ترین برش اول مصرف می‌شود و اگر وسط یک برش تمام شود،
    /// همان برش به‌نسبت تقسیم می‌شود تا ارز و نرخ تاریخی‌اش دست‌نخورده بماند.
    /// </summary>
    private sealed class FifoCursor(IReadOnlyList<SupplierBalanceSourceSlice> slices)
    {
        private readonly List<SupplierBalanceSourceSlice> _slices = [.. slices];
        private int _index;
        private decimal _usedInCurrent;

        public List<SupplierBalanceTransferSource> Take(decimal originalAmount)
        {
            var taken = new List<SupplierBalanceTransferSource>();
            var remaining = originalAmount;

            while (remaining > 0m && _index < _slices.Count)
            {
                var slice = _slices[_index];
                var availableOriginal = decimal.Round(
                    slice.RemainingOriginalAmount - _usedInCurrent, 4, MidpointRounding.AwayFromZero);
                if (availableOriginal <= 0m)
                {
                    _index++;
                    _usedInCurrent = 0m;
                    continue;
                }

                var takeOriginal = Math.Min(remaining, availableOriginal);
                // ارزش دفتری به همان نسبت برداشته می‌شود تا نرخ تاریخی برش حفظ شود.
                var takeBookUsd = takeOriginal == slice.RemainingOriginalAmount
                    ? slice.RemainingBookAmountUsd
                    : decimal.Round(
                        slice.RemainingBookAmountUsd * (takeOriginal / slice.RemainingOriginalAmount),
                        4,
                        MidpointRounding.AwayFromZero);

                taken.Add(new SupplierBalanceTransferSource
                {
                    SourceType = slice.SourceType,
                    SourceId = slice.SourceId,
                    LedgerEntryId = slice.LedgerEntryId,
                    SourceDate = slice.SourceDate,
                    ConsumedOriginalAmount = takeOriginal,
                    OriginalCurrencyCode = slice.CurrencyCode,
                    HistoricalFxRateToUsd = slice.HistoricalFxRateToUsd,
                    // نرخِ خودِ همین منبع قفل می‌شود، نه نرخ خلاصهٔ سطل. برگشت انتقال
                    // بعداً از همین snapshot استفاده می‌کند.
                    HistoricalCurrencyPerUsdRate = slice.HistoricalCurrencyPerUsdRate,
                    ConsumedBookAmountUsd = takeBookUsd
                });

                remaining = decimal.Round(remaining - takeOriginal, 4, MidpointRounding.AwayFromZero);
                _usedInCurrent = decimal.Round(_usedInCurrent + takeOriginal, 4, MidpointRounding.AwayFromZero);
                if (_usedInCurrent >= slice.RemainingOriginalAmount)
                {
                    _index++;
                    _usedInCurrent = 0m;
                }
            }

            if (remaining > 0m)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_BALANCE_TRANSFER_EXCEEDS_BALANCE",
                    "مانده کافی نیست.");
            }

            return taken;
        }
    }
}
