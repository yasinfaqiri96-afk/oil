using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public sealed record ContractBalanceTransferCreateRequest(
    DateTime TransferDate,
    int FromContractId,
    int ToContractId,
    decimal AmountOriginal,
    string CurrencyCode,
    decimal FxRateToUsd,
    DateTime? FxRateDate,
    string? FxRateSource,
    int? OriginalPaymentTransactionId,
    decimal? OriginalPaymentFxRateToUsd,
    string? Reference,
    string? Notes);

public interface IContractBalanceTransferService
{
    Task<ContractBalanceTransfer> CreateAsync(
        ContractBalanceTransferCreateRequest request,
        CancellationToken ct = default);

    Task<decimal> GetContractNetBalanceUsdAsync(int contractId, CancellationToken ct = default);
}

public sealed class ContractBalanceTransferService : IContractBalanceTransferService
{
    public const string LedgerSourceType = "ContractBalanceTransfer";

    private readonly ApplicationDbContext _db;
    private readonly IContractBalanceTransferAccountingAdapter? _accountingAdapter;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);

    public ContractBalanceTransferService(
        ApplicationDbContext db,
        IContractBalanceTransferAccountingAdapter? accountingAdapter = null)
    {
        _db = db;
        _accountingAdapter = accountingAdapter;
    }

    public async Task<ContractBalanceTransfer> CreateAsync(
        ContractBalanceTransferCreateRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_CURRENCY_REQUIRED",
                "ارز انتقال الزامی است.");
        }

        var normalized = Normalize(request);
        var (fromContract, toContract) = await ValidateAsync(normalized, ct);
        var amountUsd = decimal.Round(normalized.AmountOriginal * normalized.FxRateToUsd, 4, MidpointRounding.AwayFromZero);

        var availableBalance = await GetContractNetBalanceUsdAsync(normalized.FromContractId, ct);
        if (amountUsd > availableBalance)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_INSUFFICIENT_BALANCE",
                "مانده این قرارداد برای انتقال کافی نیست.");
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var transfer = new ContractBalanceTransfer
            {
                TransferDate = normalized.TransferDate.Date,
                FromContractId = normalized.FromContractId,
                ToContractId = normalized.ToContractId,
                AmountOriginal = normalized.AmountOriginal,
                CurrencyCode = normalized.CurrencyCode,
                FxRateToUsd = normalized.FxRateToUsd,
                AmountUsd = amountUsd,
                FxRateDate = normalized.FxRateDate?.Date,
                FxRateSource = normalized.FxRateSource,
                OriginalPaymentTransactionId = normalized.OriginalPaymentTransactionId,
                OriginalPaymentFxRateToUsd = normalized.OriginalPaymentFxRateToUsd,
                Reference = normalized.Reference,
                Notes = normalized.Notes,
                IsCancelled = false
            };

            _db.ContractBalanceTransfers.Add(transfer);
            await _db.SaveChangesAsync(ct);

            Ledger.PostRange(
                BuildLedgerEntry(
                    transfer,
                    LedgerSide.Debit,
                    transfer.FromContractId,
                    $"Transfer to contract {toContract.ContractNumber}"),
                BuildLedgerEntry(
                    transfer,
                    LedgerSide.Credit,
                    transfer.ToContractId,
                    $"Transfer from contract {fromContract.ContractNumber}"));

            await _db.SaveChangesAsync(ct);

            if (_accountingAdapter is not null)
            {
                await _accountingAdapter.TryPostAsync(
                    transfer,
                    fromContract,
                    toContract,
                    ct);
            }

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

    public async Task<decimal> GetContractNetBalanceUsdAsync(int contractId, CancellationToken ct = default)
    {
        var entries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .Select(l => new { l.Side, l.AmountUsd })
            .ToListAsync(ct);

        return entries.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
    }

    private async Task<(Contract FromContract, Contract ToContract)> ValidateAsync(
        ContractBalanceTransferCreateRequest request,
        CancellationToken ct)
    {
        if (request.FromContractId == request.ToContractId)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_SAME_CONTRACT",
                "قرارداد مبدا و مقصد نباید یکی باشند.");
        }

        if (request.AmountOriginal <= 0m)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_AMOUNT_INVALID",
                "مبلغ انتقال باید بزرگ‌تر از صفر باشد.");
        }

        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_CURRENCY_REQUIRED",
                "ارز انتقال الزامی است.");
        }

        if (request.FxRateToUsd <= 0m)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_FX_INVALID",
                "نرخ تبدیل به USD باید بزرگ‌تر از صفر باشد.");
        }

        var fromContract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.FromContractId, ct);
        if (fromContract is null)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_FROM_NOT_FOUND",
                "قرارداد مبدا معتبر نیست.");
        }

        var toContract = await _db.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ToContractId, ct);
        if (toContract is null)
        {
            throw new BusinessRuleException(
                "CONTRACT_BALANCE_TRANSFER_TO_NOT_FOUND",
                "قرارداد مقصد معتبر نیست.");
        }

        if (request.OriginalPaymentTransactionId.HasValue)
        {
            var paymentExists = await _db.PaymentTransactions
                .AsNoTracking()
                .AnyAsync(p => p.Id == request.OriginalPaymentTransactionId.Value, ct);
            if (!paymentExists)
            {
                throw new BusinessRuleException(
                    "CONTRACT_BALANCE_TRANSFER_PAYMENT_NOT_FOUND",
                    "پرداخت اولیه انتخاب‌شده معتبر نیست.");
            }
        }

        return (fromContract, toContract);
    }

    private static ContractBalanceTransferCreateRequest Normalize(ContractBalanceTransferCreateRequest request)
    {
        var currencyCode = SystemCurrency.Normalize(request.CurrencyCode);
        var fxRate = SystemCurrency.IsBaseCurrency(currencyCode) ? 1m : request.FxRateToUsd;

        return request with
        {
            TransferDate = request.TransferDate.Date,
            CurrencyCode = currencyCode,
            FxRateToUsd = fxRate,
            FxRateDate = request.FxRateDate?.Date,
            FxRateSource = string.IsNullOrWhiteSpace(request.FxRateSource) ? null : request.FxRateSource.Trim(),
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };
    }

    private static LedgerPostingRequest BuildLedgerEntry(
        ContractBalanceTransfer transfer,
        LedgerSide side,
        int contractId,
        string description)
        => new()
        {
            EntryDate = transfer.TransferDate.Date,
            Side = side,
            AmountUsd = transfer.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = transfer.AmountOriginal,
            SourceCurrencyCode = transfer.CurrencyCode,
            AppliedFxRateToUsd = transfer.FxRateToUsd,
            AppliedFxRateDate = transfer.FxRateDate ?? transfer.TransferDate.Date,
            AppliedFxRateSource = transfer.FxRateSource,
            Description = description,
            SourceType = LedgerSourceType,
            SourceId = transfer.Id,
            Reference = transfer.Reference,
            ContractId = contractId
        };
}
