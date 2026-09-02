using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public sealed record SupplierPaymentAllocationCreateRequest(
    int PaymentTransactionId,
    int ContractId,
    DateTime AllocationDate,
    decimal AllocatedPaymentAmount,
    decimal ContractCurrencyPerUsdRate,
    string? ReferenceNumber,
    string? Notes,
    string? CreatedByUserName,
    // «در تاریخ تخصیص، هر ۱ دلار چند واحد از ارز پرداخت است؟» (مثلاً 100 برای RUB).
    // null یعنی «نرخ روز تخصیص را همان نرخ روز پرداخت بگیر» — رفتار قبلی، بدون سود/زیان تسعیر.
    decimal? PaymentCurrencyPerUsdRateAtAllocation = null);

public sealed record SupplierPaymentAllocationReverseRequest(
    int AllocationId,
    string ReversalReason,
    string? ReversedByUserName);

public interface ISupplierPaymentAllocationService
{
    Task<decimal> GetAllocatableBalanceUsdAsync(int paymentTransactionId, CancellationToken ct = default);

    /// <summary>مانده واقعی به ارز پرداخت (مثلاً RUB) — مبنای کنترل over-allocation.</summary>
    Task<decimal> GetAllocatablePaymentAmountAsync(int paymentTransactionId, CancellationToken ct = default);
    Task<SupplierPaymentAllocation> CreateAsync(SupplierPaymentAllocationCreateRequest request, CancellationToken ct = default);
    Task<SupplierPaymentAllocation> ReverseAsync(SupplierPaymentAllocationReverseRequest request, CancellationToken ct = default);
}

/// <summary>
/// تخصیص پیش‌پرداخت آزاد تأمین‌کننده به یک قرارداد خرید.
///
/// این یک «پرداخت جدید» نیست؛ فقط بخشی از پیش‌پرداخت آزاد را به قرارداد منتقل می‌کند.
/// پیش‌پرداخت با ارز اصلی خودش (مثلاً RUB) نگه داشته می‌شود و در تاریخ تخصیص با نرخ همان
/// روز به ارز قرارداد تبدیل می‌شود. بنابراین هر تخصیص تا سه LedgerEntry متوازن می‌سازد:
///   - Credit با ContractId = null  → کاهش پیش‌پرداخت آزاد به «ارزش تاریخی» (نرخ روز پرداخت)
///   - Debit  با ContractId = قرارداد → تسویه قرارداد به «ارزش روز تخصیص» (نرخ روز تخصیص)
///   - سطر سوم فقط وقتی این دو ارزش فرق دارند: سود/زیان تسعیر (بدون طرف‌حساب → P&L)
///     با همان الگوی SarrafSettlement: زیان = Debit، سود = Credit.
/// وقتی نرخ روز تخصیص با نرخ روز پرداخت یکی باشد (یا پرداخت دالری باشد) اختلاف صفر است و
/// رفتار دقیقاً مثل قبل باقی می‌ماند: فقط دو سطر متوازن با اثر خالص صفر.
///
/// نرخ‌ها و تمام مبالغ هنگام ثبت قفل می‌شوند و رکورد تخصیص ویرایش/حذف نمی‌شود؛
/// اصلاح فقط از طریق «برگشت تخصیص» با ثبت‌های معکوس (شامل سطر تسعیر) انجام می‌شود.
/// </summary>
public sealed class SupplierPaymentAllocationService : ISupplierPaymentAllocationService
{
    public const string LedgerSourceType = "SupplierPaymentAllocation";
    public const string ReversalLedgerSourceType = "SupplierPaymentAllocationReversal";
    public const string ExchangeDifferenceLedgerSourceType = "SupplierPaymentAllocationExchangeDifference";
    public const string ExchangeDifferenceReversalLedgerSourceType = "SupplierPaymentAllocationExchangeDifferenceReversal";

    private readonly ApplicationDbContext _db;
    private readonly ISupplierPaymentAllocationAccountingAdapter? _accountingAdapter;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);

    public SupplierPaymentAllocationService(
        ApplicationDbContext db,
        ISupplierPaymentAllocationAccountingAdapter? accountingAdapter = null)
    {
        _db = db;
        _accountingAdapter = accountingAdapter;
    }

    public async Task<decimal> GetAllocatableBalanceUsdAsync(int paymentTransactionId, CancellationToken ct = default)
    {
        var paymentUsd = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.Id == paymentTransactionId)
            .Select(p => (decimal?)p.AmountUsd)
            .FirstOrDefaultAsync(ct);

        if (paymentUsd is null)
        {
            return 0m;
        }

        var allocated = await _db.SupplierPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PaymentTransactionId == paymentTransactionId && a.Status == SupplierPaymentAllocationStatus.Active)
            .SumAsync(a => (decimal?)a.AllocatedBookAmountUsd, ct) ?? 0m;
        return decimal.Round(paymentUsd.Value - allocated, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<decimal> GetAllocatablePaymentAmountAsync(int paymentTransactionId, CancellationToken ct = default)
    {
        var paymentAmount = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.Id == paymentTransactionId)
            .Select(p => (decimal?)p.Amount)
            .FirstOrDefaultAsync(ct);

        if (paymentAmount is null)
        {
            return 0m;
        }

        var allocated = await _db.SupplierPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PaymentTransactionId == paymentTransactionId && a.Status == SupplierPaymentAllocationStatus.Active)
            .SumAsync(a => (decimal?)a.AllocatedPaymentAmount, ct) ?? 0m;
        return decimal.Round(paymentAmount.Value - allocated, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<SupplierPaymentAllocation> CreateAsync(
        SupplierPaymentAllocationCreateRequest request,
        CancellationToken ct = default)
    {
        if (request.AllocatedPaymentAmount <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_AMOUNT_INVALID",
                "مبلغ مصرف‌شده باید بزرگ‌تر از صفر باشد.");
        }

        if (request.ContractCurrencyPerUsdRate <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_RATE_INVALID",
                "نرخ تبدیل ارز قرارداد باید بزرگ‌تر از صفر باشد.");
        }

        if (request.PaymentCurrencyPerUsdRateAtAllocation is <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_ALLOCATION_RATE_INVALID",
                "نرخ روز تخصیص باید بزرگ‌تر از صفر باشد.");
        }

        var payment = await _db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Id == request.PaymentTransactionId, ct)
            ?? throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_PAYMENT_NOT_FOUND",
                "پرداخت انتخاب‌شده معتبر نیست.");

        if (!payment.SupplierId.HasValue || payment.PaymentKind != PaymentKind.SupplierPayment)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_NOT_SUPPLIER",
                "فقط «پرداخت به تأمین‌کننده» قابل تخصیص به قرارداد است.");
        }

        var contract = await _db.Contracts
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, ct)
            ?? throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_CONTRACT_NOT_FOUND",
                "قرارداد انتخاب‌شده معتبر نیست.");

        if (contract.ContractType != ContractType.Purchase)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_CONTRACT_NOT_PURCHASE",
                "قرارداد باید قرارداد خرید باشد.");
        }

        if (contract.SupplierId != payment.SupplierId)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_SUPPLIER_MISMATCH",
                "قرارداد باید متعلق به همان تأمین‌کنندهٔ پرداخت باشد.");
        }

        // نرخ پرداخت قفل‌شده (برای USD برابر 1). کنوانسیون: AmountUsd = AmountOriginal × FxRateToUsd.
        var paymentFxRateToUsd = payment.AppliedFxRateToUsd ?? 1m;
        var bookAmountUsd = decimal.Round(request.AllocatedPaymentAmount * paymentFxRateToUsd, 4, MidpointRounding.AwayFromZero);
        if (bookAmountUsd <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_AMOUNT_INVALID",
                "مبلغ مصرف‌شده باید بزرگ‌تر از صفر باشد.");
        }

        var paymentCurrency = SystemCurrency.Normalize(payment.Currency);
        var isPaymentUsd = SystemCurrency.IsBaseCurrency(paymentCurrency);

        // نرخ روز تخصیص: «۱ دلار = چند واحد ارز پرداخت». برای پرداخت دالری همیشه ۱ است و
        // اگر کاربر نرخی نداده باشد، همان نرخ روز پرداخت استفاده می‌شود (اختلاف تسعیر صفر).
        var paymentPerUsdAtPayment = decimal.Round(1m / paymentFxRateToUsd, 6, MidpointRounding.AwayFromZero);
        var paymentPerUsdAtAllocation = isPaymentUsd
            ? 1m
            : request.PaymentCurrencyPerUsdRateAtAllocation ?? paymentPerUsdAtPayment;

        // ارزش همان مبلغ ارز پرداخت با نرخ روز تخصیص: 200 RUB ÷ 100 = 2 USD.
        // عمداً با کنوانسیون «مبلغ × نرخ» حساب می‌شود (نه تقسیم مستقیم) تا با اعتبارسنجی
        // AccountingPostingService که همین ضرب را دوباره چک می‌کند، دقیقاً یکی باشد.
        var paymentFxRateToUsdAtAllocation = isPaymentUsd
            ? 1m
            : decimal.Round(1m / paymentPerUsdAtAllocation, 6, MidpointRounding.AwayFromZero);
        var valueUsdAtAllocation = isPaymentUsd
            ? bookAmountUsd
            : decimal.Round(request.AllocatedPaymentAmount * paymentFxRateToUsdAtAllocation, 4, MidpointRounding.AwayFromZero);
        if (valueUsdAtAllocation <= 0m)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_AMOUNT_INVALID",
                "ارزش تخصیص در تاریخ تخصیص باید بزرگ‌تر از صفر باشد.");
        }

        // اختلاف تسعیر: مثبت = سود (ارز پرداخت قوی‌تر شده)، منفی = زیان.
        var exchangeDifferenceUsd = decimal.Round(valueUsdAtAllocation - bookAmountUsd, 4, MidpointRounding.AwayFromZero);
        var exchangeDifferenceType = exchangeDifferenceUsd switch
        {
            > 0m => SarrafSettlementDifferenceType.Gain,
            < 0m => SarrafSettlementDifferenceType.Loss,
            _ => SarrafSettlementDifferenceType.None
        };

        var contractCurrency = SystemCurrency.Normalize(contract.Currency);
        var isContractUsd = SystemCurrency.IsBaseCurrency(contractCurrency);
        var perUsdRate = isContractUsd ? 1m : request.ContractCurrencyPerUsdRate;
        var contractFxRateToUsd = isContractUsd
            ? 1m
            : decimal.Round(1m / perUsdRate, 6, MidpointRounding.AwayFromZero);
        // قرارداد با ارزش روز تخصیص تسویه می‌شود، نه با ارزش تاریخی پیش‌پرداخت.
        var contractCurrencyAmount = decimal.Round(valueUsdAtAllocation * perUsdRate, 4, MidpointRounding.AwayFromZero);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            // محاسبهٔ مانده قابل تخصیص داخل transaction تا تخصیص هم‌زمان باعث over-allocation نشود.
            // مبنا، مانده واقعی «ارز پرداخت» است؛ ارزش دلاری با نرخ روز تخصیص تغییر می‌کند و
            // نمی‌تواند سقف مصرفِ خودِ ارز باشد.
            var alreadyAllocatedAmount = await _db.SupplierPaymentAllocations
                .Where(a => a.PaymentTransactionId == payment.Id && a.Status == SupplierPaymentAllocationStatus.Active)
                .SumAsync(a => (decimal?)a.AllocatedPaymentAmount, ct) ?? 0m;
            var allocatableAmount = decimal.Round(payment.Amount - alreadyAllocatedAmount, 4, MidpointRounding.AwayFromZero);

            if (request.AllocatedPaymentAmount > allocatableAmount)
            {
                throw new BusinessRuleException(
                    "SUPPLIER_PAYMENT_ALLOCATION_EXCEEDS_BALANCE",
                    $"مبلغ مصرف‌شده از مانده قابل تخصیص بیشتر است. مانده فعلی: {allocatableAmount:N2} {paymentCurrency}.");
            }

            var allocation = new SupplierPaymentAllocation
            {
                PaymentTransactionId = payment.Id,
                ContractId = contract.Id,
                AllocationDate = request.AllocationDate.Date,
                AllocatedPaymentAmount = request.AllocatedPaymentAmount,
                PaymentCurrencyCode = paymentCurrency,
                PaymentFxRateToUsd = paymentFxRateToUsd,
                AllocatedBookAmountUsd = bookAmountUsd,
                PaymentCurrencyPerUsdRateAtAllocation = paymentPerUsdAtAllocation,
                PaymentCurrencyFxRateToUsdAtAllocation = paymentFxRateToUsdAtAllocation,
                AllocatedValueUsdAtAllocation = valueUsdAtAllocation,
                ExchangeDifferenceUsd = exchangeDifferenceUsd,
                ExchangeDifferenceType = exchangeDifferenceType,
                ContractCurrencyCode = contractCurrency,
                ContractCurrencyPerUsdRate = perUsdRate,
                ContractCurrencyFxRateToUsd = contractFxRateToUsd,
                AllocatedContractCurrencyAmount = contractCurrencyAmount,
                ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? null : request.ReferenceNumber.Trim(),
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                Status = SupplierPaymentAllocationStatus.Active,
                CreatedByUserName = string.IsNullOrWhiteSpace(request.CreatedByUserName) ? null : request.CreatedByUserName.Trim()
            };

            _db.SupplierPaymentAllocations.Add(allocation);
            await _db.SaveChangesAsync(ct);

            Ledger.PostRange(
                // ارزش تاریخی از پیش‌پرداخت آزاد خارج می‌شود تا باقی‌ماندهٔ موهومی نماند.
                BuildLedgerEntry(
                    allocation,
                    payment.SupplierId.Value,
                    LedgerSide.Credit,
                    contractId: null,
                    amountUsd: allocation.AllocatedBookAmountUsd,
                    sourceAmount: allocation.AllocatedPaymentAmount,
                    sourceCurrency: allocation.PaymentCurrencyCode,
                    appliedFxRateToUsd: allocation.PaymentFxRateToUsd,
                    sourceType: LedgerSourceType,
                    description: $"کاهش پیش‌پرداخت آزاد تأمین‌کننده بابت تخصیص به قرارداد {contract.ContractNumber}"),
                // قرارداد با ارزش روز تخصیص تسویه می‌شود.
                BuildLedgerEntry(
                    allocation,
                    payment.SupplierId.Value,
                    LedgerSide.Debit,
                    contractId: contract.Id,
                    amountUsd: allocation.AllocatedValueUsdAtAllocation,
                    sourceAmount: allocation.AllocatedContractCurrencyAmount,
                    sourceCurrency: allocation.ContractCurrencyCode,
                    appliedFxRateToUsd: allocation.ContractCurrencyFxRateToUsd,
                    sourceType: LedgerSourceType,
                    description: $"انتقال پیش‌پرداخت به قرارداد {contract.ContractNumber} با نرخ روز تخصیص"));

            await _db.SaveChangesAsync(ct);

            // سطر سوم فقط وقتی لازم است که ارزش روز تخصیص با ارزش تاریخی فرق کند؛ بدون آن
            // مجموع Debit و Credit برابر نمی‌ماند. الگو: SarrafSettlement (زیان=Debit، سود=Credit).
            if (allocation.ExchangeDifferenceType != SarrafSettlementDifferenceType.None)
            {
                var differenceLedger = Ledger.Post(
                    BuildExchangeDifferenceLedger(allocation, contract, ExchangeDifferenceLedgerSourceType));
                await _db.SaveChangesAsync(ct);

                allocation.ExchangeDifferenceLedgerEntryId = differenceLedger.Id;
                await _db.SaveChangesAsync(ct);
            }

            // Dual-write pilot: journal + legacy ledgers share this transaction, so a
            // posting failure rolls back the whole allocation.
            if (_accountingAdapter is not null)
            {
                await _accountingAdapter.TryPostAllocationAsync(allocation, payment, contract, ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return allocation;
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

    public async Task<SupplierPaymentAllocation> ReverseAsync(
        SupplierPaymentAllocationReverseRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReversalReason))
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_REVERSAL_REASON_REQUIRED",
                "دلیل برگشت تخصیص الزامی است.");
        }

        var allocation = await _db.SupplierPaymentAllocations
            .Include(a => a.Contract)
            .Include(a => a.PaymentTransaction)
            .FirstOrDefaultAsync(a => a.Id == request.AllocationId, ct)
            ?? throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_NOT_FOUND",
                "تخصیص انتخاب‌شده معتبر نیست.");

        if (allocation.Status != SupplierPaymentAllocationStatus.Active)
        {
            throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_ALREADY_REVERSED",
                "این تخصیص قبلاً برگشت داده شده است.");
        }

        var supplierId = allocation.PaymentTransaction?.SupplierId
            ?? throw new BusinessRuleException(
                "SUPPLIER_PAYMENT_ALLOCATION_NOT_SUPPLIER",
                "پرداخت این تخصیص تأمین‌کننده ندارد.");

        // Compose inside a caller's transaction (e.g. a payment correction) when one is already
        // open; otherwise own one. Matches AccountingPostingService's nesting guard.
        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            allocation.Status = SupplierPaymentAllocationStatus.Reversed;
            allocation.ReversedAtUtc = DateTime.UtcNow;
            allocation.ReversedByUserName = string.IsNullOrWhiteSpace(request.ReversedByUserName)
                ? null
                : request.ReversedByUserName.Trim();
            allocation.ReversalReason = request.ReversalReason.Trim();

            // ثبت‌های معکوس و جداگانه؛ Ledgerهای اصلی حذف یا ویرایش نمی‌شوند.
            Ledger.PostRange(
                BuildLedgerEntry(
                    allocation,
                    supplierId,
                    LedgerSide.Debit,
                    contractId: null,
                    amountUsd: allocation.AllocatedBookAmountUsd,
                    sourceAmount: allocation.AllocatedPaymentAmount,
                    sourceCurrency: allocation.PaymentCurrencyCode,
                    appliedFxRateToUsd: allocation.PaymentFxRateToUsd,
                    sourceType: ReversalLedgerSourceType,
                    description: $"برگشت تخصیص پیش‌پرداخت — بازگشت به پیش‌پرداخت آزاد (#{allocation.Id})"),
                BuildLedgerEntry(
                    allocation,
                    supplierId,
                    LedgerSide.Credit,
                    contractId: allocation.ContractId,
                    amountUsd: allocation.AllocatedValueUsdAtAllocation,
                    sourceAmount: allocation.AllocatedContractCurrencyAmount,
                    sourceCurrency: allocation.ContractCurrencyCode,
                    appliedFxRateToUsd: allocation.ContractCurrencyFxRateToUsd,
                    sourceType: ReversalLedgerSourceType,
                    description: $"برگشت تخصیص پیش‌پرداخت از قرارداد (#{allocation.Id})"));

            // سود/زیان تسعیر هم باید معکوس شود، وگرنه ثبت‌های برگشت نامتوازن می‌مانند.
            if (allocation.ExchangeDifferenceType != SarrafSettlementDifferenceType.None)
            {
                Ledger.Post(BuildExchangeDifferenceLedger(
                    allocation,
                    allocation.Contract,
                    ExchangeDifferenceReversalLedgerSourceType,
                    reverse: true));
            }

            await _db.SaveChangesAsync(ct);

            // Dual-write pilot: independent reversal journal; the original journal is
            // never edited or deleted. Shares this transaction with the legacy rows.
            if (_accountingAdapter is not null
                && allocation.PaymentTransaction is not null
                && allocation.Contract is not null)
            {
                await _accountingAdapter.TryPostReversalAsync(
                    allocation,
                    allocation.PaymentTransaction,
                    allocation.Contract,
                    ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return allocation;
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
    /// سطر سود/زیان تسعیر تخصیص — بدون طرف‌حساب تا روی صورت‌حساب تأمین‌کننده ننشیند و
    /// به‌عنوان اثر P&L شناخته شود. دقیقاً همان قرارداد علامت‌گذاری SarrafSettlement:
    /// زیان = Debit، سود = Credit؛ در حالت برگشت، سمت آن معکوس می‌شود.
    /// </summary>
    private static LedgerPostingRequest BuildExchangeDifferenceLedger(
        SupplierPaymentAllocation allocation,
        Contract? contract,
        string sourceType,
        bool reverse = false)
    {
        var isLoss = allocation.ExchangeDifferenceType == SarrafSettlementDifferenceType.Loss;
        var amount = Math.Abs(allocation.ExchangeDifferenceUsd);
        var side = isLoss ? LedgerSide.Debit : LedgerSide.Credit;
        if (reverse)
        {
            side = side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit;
        }

        var label = isLoss ? "زیان تسعیر تخصیص پیش‌پرداخت" : "سود تسعیر تخصیص پیش‌پرداخت";
        var contractNumber = contract?.ContractNumber;
        var description = reverse
            ? $"برگشت {label} (#{allocation.Id})"
            : string.IsNullOrWhiteSpace(contractNumber)
                ? $"{label} (#{allocation.Id})"
                : $"{label} بابت قرارداد {contractNumber}";

        return new LedgerPostingRequest
        {
            EntryDate = allocation.AllocationDate.Date,
            Side = side,
            AmountUsd = amount,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amount,
            SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = allocation.AllocationDate.Date,
            AppliedFxRateSource = sourceType,
            Description = description,
            SourceType = sourceType,
            SourceId = allocation.Id,
            Reference = allocation.ReferenceNumber,
            ContractId = allocation.ContractId
        };
    }

    private static LedgerPostingRequest BuildLedgerEntry(
        SupplierPaymentAllocation allocation,
        int supplierId,
        LedgerSide side,
        int? contractId,
        decimal amountUsd,
        decimal sourceAmount,
        string sourceCurrency,
        decimal appliedFxRateToUsd,
        string sourceType,
        string description)
        => new()
        {
            EntryDate = allocation.AllocationDate.Date,
            Side = side,
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = sourceAmount,
            SourceCurrencyCode = sourceCurrency,
            AppliedFxRateToUsd = appliedFxRateToUsd,
            AppliedFxRateDate = allocation.AllocationDate.Date,
            AppliedFxRateSource = sourceType,
            Description = description,
            SourceType = sourceType,
            SourceId = allocation.Id,
            Reference = allocation.ReferenceNumber,
            SupplierId = supplierId,
            ContractId = contractId
        };
}
