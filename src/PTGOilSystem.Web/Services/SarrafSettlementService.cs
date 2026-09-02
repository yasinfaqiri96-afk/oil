using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public interface ISarrafSettlementService
{
    SarrafSettlementCalculation Calculate(SarrafSettlementCommand command);
    Task<SarrafSettlement> CreatePostedAsync(SarrafSettlementCommand command, CancellationToken cancellationToken = default);
    Task<SarrafSettlement> EditPostedAsync(int settlementId, SarrafSettlementCommand command, DifferenceReason? differenceReason, CancellationToken cancellationToken = default);
    Task CancelAsync(int settlementId, string? reason, CancellationToken cancellationToken = default);
}

public sealed record SarrafSettlementCommand(
    DateTime SettlementDate,
    int SarrafId,
    int? SupplierId,
    int? ContractId,
    int? PaymentTransactionId,
    int? CashAccountId,
    string? ReferenceNumber,
    string? Description,
    decimal RequestedAmount,
    string RequestedCurrency,
    decimal RequestedFxRateToUsd,
    string SarrafCurrency,
    decimal SarrafRate,
    decimal SarrafChargedAmount,
    decimal SarrafFxRateToUsd,
    decimal SupplierAcceptedAmount,
    string SupplierAcceptedCurrency,
    decimal SupplierAcceptedFxRateToUsd,
    decimal? SupplierRate,
    SarrafSettlementDifferenceTreatment DifferenceTreatment,
    // Phase 1 عمومی‌سازی: پارامترهای جدید با مقدار پیش‌فرض Out/Supplier
    // تا تمام فراخوانی‌های موضعیِ قبلی (و تست‌ها) بدون تغییر همان رفتار تأمین‌کننده را بدهند.
    SarrafSettlementDirection Direction = SarrafSettlementDirection.Out,
    SarrafSettlementCounterpartyType CounterpartyType = SarrafSettlementCounterpartyType.Supplier,
    int? CustomerId = null,
    int? ServiceProviderId = null,
    int? DriverId = null,
    int? EmployeeId = null);

public sealed record SarrafSettlementCalculation(
    decimal RequestedAmountUsd,
    decimal SarrafChargedAmountUsd,
    decimal SupplierAcceptedAmountUsd,
    decimal DifferenceAmountUsd,
    SarrafSettlementDifferenceType DifferenceType,
    decimal SupplierLedgerAmountUsd,
    decimal SupplierLedgerSourceAmount,
    string SupplierLedgerSourceCurrency,
    decimal SupplierLedgerFxRateToUsd);

public sealed class SarrafSettlementService : ISarrafSettlementService
{
    public const string SupplierLedgerSourceType = "SarrafSettlement";
    public const string ExchangeDifferenceSourceType = "SarrafSettlementExchangeDifference";
    public const string CancelSourceType = "SarrafSettlementCancel";
    public const string EditReversalSourceType = "SarrafSettlementEditReversal";

    private readonly ApplicationDbContext _db;

    // مرحلهٔ ۸ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Accounting.ISarrafSettlementAccountingAdapter? _settlementAccounting;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);

    public SarrafSettlementService(
        ApplicationDbContext db,
        Accounting.ISarrafSettlementAccountingAdapter? settlementAccounting = null)
    {
        _db = db;
        _settlementAccounting = settlementAccounting;
    }

    public SarrafSettlementCalculation Calculate(SarrafSettlementCommand command)
    {
        ValidatePositive(command.RequestedAmount, nameof(command.RequestedAmount));
        ValidatePositive(command.RequestedFxRateToUsd, nameof(command.RequestedFxRateToUsd));
        ValidatePositive(command.SarrafChargedAmount, nameof(command.SarrafChargedAmount));
        ValidatePositive(command.SarrafFxRateToUsd, nameof(command.SarrafFxRateToUsd));
        ValidatePositive(command.SupplierAcceptedAmount, nameof(command.SupplierAcceptedAmount));
        ValidatePositive(command.SupplierAcceptedFxRateToUsd, nameof(command.SupplierAcceptedFxRateToUsd));

        var requestedUsd = Money(command.RequestedAmount * command.RequestedFxRateToUsd);
        var sarrafChargedUsd = Money(command.SarrafChargedAmount * command.SarrafFxRateToUsd);
        var supplierAcceptedUsd = Money(command.SupplierAcceptedAmount * command.SupplierAcceptedFxRateToUsd);
        var differenceUsd = Money(requestedUsd - supplierAcceptedUsd);
        var differenceType = ResolveDifferenceType(differenceUsd, command.DifferenceTreatment);

        var supplierLedgerAmountUsd = command.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? requestedUsd
            : supplierAcceptedUsd;
        var supplierLedgerSourceAmount = command.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? command.RequestedAmount
            : command.SupplierAcceptedAmount;
        var supplierLedgerSourceCurrency = command.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? NormalizeCurrency(command.RequestedCurrency)
            : NormalizeCurrency(command.SupplierAcceptedCurrency);
        var supplierLedgerFxRate = command.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? command.RequestedFxRateToUsd
            : command.SupplierAcceptedFxRateToUsd;

        return new SarrafSettlementCalculation(
            requestedUsd,
            sarrafChargedUsd,
            supplierAcceptedUsd,
            differenceUsd,
            differenceType,
            supplierLedgerAmountUsd,
            supplierLedgerSourceAmount,
            supplierLedgerSourceCurrency,
            supplierLedgerFxRate);
    }

    public async Task<SarrafSettlement> CreatePostedAsync(SarrafSettlementCommand command, CancellationToken cancellationToken = default)
    {
        var calculation = Calculate(command);
        await ValidateReferencesAsync(command, cancellationToken);

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);

        var settlement = new SarrafSettlement
        {
            SettlementDate = command.SettlementDate.Date,
            Direction = command.Direction,
            CounterpartyType = command.CounterpartyType,
            SarrafId = command.SarrafId,
            SupplierId = command.SupplierId,
            CustomerId = command.CustomerId,
            ServiceProviderId = command.ServiceProviderId,
            DriverId = command.DriverId,
            EmployeeId = command.EmployeeId,
            ContractId = command.ContractId,
            PaymentTransactionId = command.PaymentTransactionId,
            CashAccountId = command.CashAccountId,
            ReferenceNumber = Clean(command.ReferenceNumber),
            Description = Clean(command.Description),
            RequestedAmount = Money(command.RequestedAmount),
            RequestedCurrency = NormalizeCurrency(command.RequestedCurrency),
            RequestedFxRateToUsd = Rate(command.RequestedFxRateToUsd),
            RequestedAmountUsd = calculation.RequestedAmountUsd,
            SarrafCurrency = NormalizeCurrency(command.SarrafCurrency),
            SarrafRate = Rate(command.SarrafRate),
            SarrafChargedAmount = Money(command.SarrafChargedAmount),
            SarrafFxRateToUsd = Rate(command.SarrafFxRateToUsd),
            SarrafChargedAmountUsd = calculation.SarrafChargedAmountUsd,
            SupplierAcceptedAmount = Money(command.SupplierAcceptedAmount),
            SupplierAcceptedCurrency = NormalizeCurrency(command.SupplierAcceptedCurrency),
            SupplierAcceptedFxRateToUsd = Rate(command.SupplierAcceptedFxRateToUsd),
            SupplierAcceptedAmountUsd = calculation.SupplierAcceptedAmountUsd,
            SupplierRate = command.SupplierRate.HasValue ? Rate(command.SupplierRate.Value) : null,
            DifferenceAmountUsd = calculation.DifferenceAmountUsd,
            DifferenceType = calculation.DifferenceType,
            DifferenceTreatment = command.DifferenceTreatment,
            Status = SarrafSettlementStatus.Posted,
            PostedAtUtc = DateTime.UtcNow
        };

        _db.SarrafSettlements.Add(settlement);
        await _db.SaveChangesAsync(cancellationToken);

        var counterpartyLedger = Ledger.Post(BuildCounterpartyLedger(settlement, calculation));
        await _db.SaveChangesAsync(cancellationToken);

        settlement.LedgerEntryId = counterpartyLedger.Id;

        if (settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            && settlement.DifferenceType != SarrafSettlementDifferenceType.None
            && settlement.DifferenceAmountUsd != 0m)
        {
            var differenceLedger = Ledger.Post(BuildExchangeDifferenceLedger(settlement));
            await _db.SaveChangesAsync(cancellationToken);
            settlement.ExchangeDifferenceLedgerEntryId = differenceLedger.Id;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementAsync(settlement, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return settlement;
    }

    public async Task<SarrafSettlement> EditPostedAsync(
        int settlementId,
        SarrafSettlementCommand command,
        DifferenceReason? differenceReason,
        CancellationToken cancellationToken = default)
    {
        var calculation = Calculate(command);
        await ValidateReferencesAsync(command, cancellationToken);

        var settlement = await _db.SarrafSettlements
            .Include(s => s.LedgerEntry)
            .Include(s => s.ExchangeDifferenceLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == settlementId, cancellationToken);

        if (settlement is null)
        {
            throw new InvalidOperationException("Sarraf settlement was not found.");
        }

        if (settlement.Status == SarrafSettlementStatus.Cancelled)
        {
            throw new InvalidOperationException("تسویهٔ لغو‌شده قابل ویرایش نیست.");
        }

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);

        // اثر دفتر کلِ قبلی را با سند معکوس برمی‌گردانیم، سپس سند جدید را با مقادیر تازه post می‌کنیم.
        if (settlement.LedgerEntry is not null)
        {
            Ledger.Post(BuildReversalLedger(settlement.LedgerEntry, settlement, "ویرایش تسویه صراف", EditReversalSourceType));
        }

        if (settlement.ExchangeDifferenceLedgerEntry is not null)
        {
            Ledger.Post(BuildReversalLedger(settlement.ExchangeDifferenceLedgerEntry, settlement, "ویرایش تسویه صراف", EditReversalSourceType));
        }

        // سند دوطرفهٔ نسخهٔ قبلی هم باید برگردد تا نسخهٔ بعدی روی یک مانده‌ی پاک بنشیند —
        // دقیقاً همان الگوی بازقیمت‌گذاری مرحلهٔ ۶. اسناد Posted تغییر نمی‌کنند؛ فقط معکوس می‌شوند.
        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementReversalAsync(settlement, cancellationToken);
        }

        settlement.SettlementDate = command.SettlementDate.Date;
        settlement.Direction = command.Direction;
        settlement.CounterpartyType = command.CounterpartyType;
        settlement.SarrafId = command.SarrafId;
        settlement.SupplierId = command.SupplierId;
        settlement.CustomerId = command.CustomerId;
        settlement.ServiceProviderId = command.ServiceProviderId;
        settlement.ContractId = command.ContractId;
        settlement.PaymentTransactionId = command.PaymentTransactionId;
        settlement.CashAccountId = command.CashAccountId;
        settlement.ReferenceNumber = Clean(command.ReferenceNumber);
        settlement.Description = Clean(command.Description);
        settlement.RequestedAmount = Money(command.RequestedAmount);
        settlement.RequestedCurrency = NormalizeCurrency(command.RequestedCurrency);
        settlement.RequestedFxRateToUsd = Rate(command.RequestedFxRateToUsd);
        settlement.RequestedAmountUsd = calculation.RequestedAmountUsd;
        settlement.SarrafCurrency = NormalizeCurrency(command.SarrafCurrency);
        settlement.SarrafRate = Rate(command.SarrafRate);
        settlement.SarrafChargedAmount = Money(command.SarrafChargedAmount);
        settlement.SarrafFxRateToUsd = Rate(command.SarrafFxRateToUsd);
        settlement.SarrafChargedAmountUsd = calculation.SarrafChargedAmountUsd;
        settlement.SupplierAcceptedAmount = Money(command.SupplierAcceptedAmount);
        settlement.SupplierAcceptedCurrency = NormalizeCurrency(command.SupplierAcceptedCurrency);
        settlement.SupplierAcceptedFxRateToUsd = Rate(command.SupplierAcceptedFxRateToUsd);
        settlement.SupplierAcceptedAmountUsd = calculation.SupplierAcceptedAmountUsd;
        settlement.SupplierRate = command.SupplierRate.HasValue ? Rate(command.SupplierRate.Value) : null;
        settlement.DifferenceAmountUsd = calculation.DifferenceAmountUsd;
        settlement.DifferenceType = calculation.DifferenceType;
        settlement.DifferenceTreatment = command.DifferenceTreatment;
        settlement.DifferenceReason = differenceReason;
        settlement.Status = SarrafSettlementStatus.Posted;
        settlement.PostedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var counterpartyLedger = Ledger.Post(BuildCounterpartyLedger(settlement, calculation));
        await _db.SaveChangesAsync(cancellationToken);
        settlement.LedgerEntryId = counterpartyLedger.Id;

        if (settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            && settlement.DifferenceType != SarrafSettlementDifferenceType.None
            && settlement.DifferenceAmountUsd != 0m)
        {
            var differenceLedger = Ledger.Post(BuildExchangeDifferenceLedger(settlement));
            await _db.SaveChangesAsync(cancellationToken);
            settlement.ExchangeDifferenceLedgerEntryId = differenceLedger.Id;
        }
        else
        {
            settlement.ExchangeDifferenceLedgerEntryId = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementAsync(settlement, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return settlement;
    }

    public async Task CancelAsync(int settlementId, string? reason, CancellationToken cancellationToken = default)
    {
        var settlement = await _db.SarrafSettlements
            .Include(s => s.LedgerEntry)
            .Include(s => s.ExchangeDifferenceLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == settlementId, cancellationToken);

        if (settlement is null)
        {
            throw new InvalidOperationException("Sarraf settlement was not found.");
        }

        if (settlement.Status == SarrafSettlementStatus.Cancelled)
        {
            return;
        }

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);

        if (settlement.LedgerEntry is not null
            && !await HasCancelLedgerAsync(settlement.Id, settlement.LedgerEntry.Id, cancellationToken))
        {
            Ledger.Post(BuildReversalLedger(settlement.LedgerEntry, settlement, reason));
        }

        if (settlement.ExchangeDifferenceLedgerEntry is not null
            && !await HasCancelLedgerAsync(settlement.Id, settlement.ExchangeDifferenceLedgerEntry.Id, cancellationToken))
        {
            Ledger.Post(BuildReversalLedger(settlement.ExchangeDifferenceLedgerEntry, settlement, reason));
        }

        settlement.Status = SarrafSettlementStatus.Cancelled;
        settlement.CancelledAtUtc = DateTime.UtcNow;
        settlement.CancelReason = Clean(reason);
        await _db.SaveChangesAsync(cancellationToken);

        if (_settlementAccounting is not null)
        {
            await _settlementAccounting.TryPostSettlementReversalAsync(settlement, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task ValidateReferencesAsync(SarrafSettlementCommand command, CancellationToken cancellationToken)
    {
        if (!await _db.Sarrafs.AsNoTracking().AnyAsync(s => s.Id == command.SarrafId && s.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Sarraf is not active or does not exist.");
        }

        Contract? contract = null;
        if (command.ContractId.HasValue)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == command.ContractId.Value, cancellationToken);
            if (contract is null)
            {
                throw new InvalidOperationException("Contract does not exist.");
            }
        }

        if (command.SupplierId.HasValue
            && !await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == command.SupplierId.Value && s.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Supplier is not active or does not exist.");
        }

        if (command.CustomerId.HasValue
            && !await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == command.CustomerId.Value, cancellationToken))
        {
            throw new InvalidOperationException("Customer does not exist.");
        }

        if (command.ServiceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == command.ServiceProviderId.Value && p.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Service provider is not active or does not exist.");
        }

        if (command.DriverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == command.DriverId.Value && d.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Driver is not active or does not exist.");
        }

        if (command.EmployeeId.HasValue
            && !await _db.Employees.AsNoTracking().AnyAsync(e => e.Id == command.EmployeeId.Value, cancellationToken))
        {
            throw new InvalidOperationException("Employee does not exist.");
        }

        // قاعدهٔ نوع/جهت: دقیقاً یک طرف‌حساب مطابق CounterpartyType باید پر باشد و بقیه خالی.
        ValidateCounterparty(command);

        if (contract is not null)
        {
            if (contract.ContractType != ContractType.Purchase)
            {
                throw new InvalidOperationException("Sarraf settlements can be attached only to purchase contracts.");
            }

            // قرارداد فقط برای تأمین‌کننده مجاز است (Phase 1).
            if (command.CounterpartyType != SarrafSettlementCounterpartyType.Supplier)
            {
                throw new InvalidOperationException("Contract can be attached only to supplier settlements.");
            }

            if (command.SupplierId.HasValue && contract.SupplierId.HasValue && contract.SupplierId.Value != command.SupplierId.Value)
            {
                throw new InvalidOperationException("Supplier does not match the selected contract.");
            }
        }

        if (command.CashAccountId.HasValue
            && !await _db.CashAccounts.AsNoTracking().AnyAsync(a => a.Id == command.CashAccountId.Value, cancellationToken))
        {
            throw new InvalidOperationException("Cash account does not exist.");
        }

        if (command.PaymentTransactionId.HasValue
            && !await _db.PaymentTransactions.AsNoTracking().AnyAsync(p => p.Id == command.PaymentTransactionId.Value, cancellationToken))
        {
            throw new InvalidOperationException("Payment transaction does not exist.");
        }
    }

    // اعتبارسنجی طرف‌حساب: دقیقاً یک FK مطابق نوع پر باشد و بقیه null.
    // جهت (پرداخت/دریافت) آزاد است؛ سمت دفتر کل در BuildCounterpartyLedger از روی جهت و نوع تعیین می‌شود.
    private static void ValidateCounterparty(SarrafSettlementCommand command)
    {
        var supplier = command.SupplierId.HasValue;
        var customer = command.CustomerId.HasValue;
        var serviceProvider = command.ServiceProviderId.HasValue;
        var driver = command.DriverId.HasValue;
        var employee = command.EmployeeId.HasValue;
        var setCount = (supplier ? 1 : 0) + (customer ? 1 : 0) + (serviceProvider ? 1 : 0) + (driver ? 1 : 0) + (employee ? 1 : 0);

        bool required = command.CounterpartyType switch
        {
            SarrafSettlementCounterpartyType.Supplier => supplier,
            SarrafSettlementCounterpartyType.Customer => customer,
            SarrafSettlementCounterpartyType.ServiceProvider => serviceProvider,
            SarrafSettlementCounterpartyType.Driver => driver,
            SarrafSettlementCounterpartyType.Employee => employee,
            _ => throw new InvalidOperationException("Unsupported counterparty type.")
        };

        if (!required)
        {
            throw new InvalidOperationException($"A {command.CounterpartyType} counterparty is required for this settlement.");
        }

        if (setCount != 1)
        {
            throw new InvalidOperationException("Exactly one counterparty may be set, matching the counterparty type.");
        }
    }

    // سند دفتر کلِ طرف‌حساب. سمت از روی جهت و نوع تعیین می‌شود (مطابق قرارداد GetLedgerSide نقدی):
    //   تأمین‌کننده/شرکت خدماتی/راننده/کارمند: پرداخت(Out)→Debit، دریافت(In)→Credit؛
    //   مشتری: دریافت(In)→Debit، پرداخت/برگشت(Out)→Credit.
    // فقط FK طرف عوض می‌شود؛ مبالغ/ارز/نرخ دقیقاً مثل مسیر تأمین‌کننده می‌مانند (حفظ RUB/SourceAmount).
    private static LedgerPostingRequest BuildCounterpartyLedger(SarrafSettlement settlement, SarrafSettlementCalculation calculation)
    {
        // PTG-P1-03 — همان مقادیر قبلی. ارجاعِ طرف مقابل که قبلاً بعد از ساخت روی شیء
        // نوشته می‌شد، حالا پیش از ساخت تعیین و در همان درخواست گذاشته می‌شود.
        var request = new LedgerPostingRequest
        {
            EntryDate = settlement.SettlementDate,
            Side = CounterpartyLedgerSide(settlement.CounterpartyType, settlement.Direction),
            AmountUsd = calculation.SupplierLedgerAmountUsd,
            Currency = "USD",
            SourceAmount = Money(calculation.SupplierLedgerSourceAmount),
            SourceCurrencyCode = calculation.SupplierLedgerSourceCurrency,
            AppliedFxRateToUsd = Rate(calculation.SupplierLedgerFxRateToUsd),
            AppliedFxRateDate = settlement.SettlementDate,
            AppliedFxRateSource = "Sarraf settlement",
            Description = BuildDescription(settlement, CounterpartyLedgerDescription(settlement.CounterpartyType)),
            SourceType = SupplierLedgerSourceType,
            SourceId = settlement.Id,
            Reference = settlement.ReferenceNumber
        };

        return settlement.CounterpartyType switch
        {
            SarrafSettlementCounterpartyType.Supplier => request with
            {
                SupplierId = settlement.SupplierId,
                ContractId = settlement.ContractId, // قرارداد فقط برای تأمین‌کننده
            },
            SarrafSettlementCounterpartyType.Customer => request with { CustomerId = settlement.CustomerId },
            SarrafSettlementCounterpartyType.ServiceProvider => request with { ServiceProviderId = settlement.ServiceProviderId },
            SarrafSettlementCounterpartyType.Driver => request with { DriverId = settlement.DriverId },
            SarrafSettlementCounterpartyType.Employee => request with { EmployeeId = settlement.EmployeeId },
            _ => request,
        };
    }

    // سمت دفتر کل طرف مقابل بر اساس جهت و نوع، هم‌سو با GetLedgerSide مسیر نقدی:
    // مشتری (حساب طلب): دریافت=Debit، برگشت/پرداخت=Credit.
    // بقیه (حساب بدهی): پرداخت=Debit، دریافت/برگشت=Credit.
    private static LedgerSide CounterpartyLedgerSide(SarrafSettlementCounterpartyType type, SarrafSettlementDirection direction)
    {
        var reduces = type == SarrafSettlementCounterpartyType.Customer
            ? direction == SarrafSettlementDirection.In
            : direction == SarrafSettlementDirection.Out;
        return reduces ? LedgerSide.Debit : LedgerSide.Credit;
    }

    private static string CounterpartyLedgerDescription(SarrafSettlementCounterpartyType type)
        => type switch
        {
            SarrafSettlementCounterpartyType.Customer => "Sarraf settlement customer receivable reduction",
            SarrafSettlementCounterpartyType.ServiceProvider => "Sarraf settlement service-provider reduction",
            SarrafSettlementCounterpartyType.Driver => "Sarraf settlement driver payable reduction",
            SarrafSettlementCounterpartyType.Employee => "Sarraf settlement employee payable reduction",
            _ => "Sarraf settlement supplier reduction"
        };

    private static LedgerPostingRequest BuildExchangeDifferenceLedger(SarrafSettlement settlement)
    {
        var isLoss = settlement.DifferenceType == SarrafSettlementDifferenceType.Loss
            || settlement.DifferenceType == SarrafSettlementDifferenceType.SupplierShortfall;
        var amount = Money(Math.Abs(settlement.DifferenceAmountUsd));

        return new LedgerPostingRequest
        {
            EntryDate = settlement.SettlementDate,
            Side = isLoss ? LedgerSide.Debit : LedgerSide.Credit,
            AmountUsd = amount,
            Currency = "USD",
            SourceAmount = amount,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = settlement.SettlementDate,
            AppliedFxRateSource = "Sarraf settlement difference",
            Description = BuildDescription(settlement, isLoss ? "Sarraf exchange loss" : "Sarraf exchange gain"),
            SourceType = ExchangeDifferenceSourceType,
            SourceId = settlement.Id,
            Reference = settlement.ReferenceNumber,
            ContractId = settlement.ContractId
        };
    }

    private static LedgerPostingRequest BuildReversalLedger(LedgerEntry original, SarrafSettlement settlement, string? reason, string sourceType = CancelSourceType)
        => new()
        {
            EntryDate = AfghanistanBusinessClock.SystemToday,
            Side = original.Side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceAmount = original.SourceAmount,
            SourceCurrencyCode = original.SourceCurrencyCode,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            AppliedFxRateDate = original.AppliedFxRateDate,
            AppliedFxRateSource = original.AppliedFxRateSource,
            Description = string.IsNullOrWhiteSpace(reason)
                ? $"Cancel Sarraf settlement ledger #{original.Id}"
                : $"Cancel Sarraf settlement ledger #{original.Id}: {reason}",
            SourceType = sourceType,
            SourceId = settlement.Id,
            Reference = settlement.ReferenceNumber,
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            SupplierId = original.SupplierId,
            ServiceProviderId = original.ServiceProviderId,
            EmployeeId = original.EmployeeId,
            ShipmentId = original.ShipmentId
        };

    private async Task<bool> HasCancelLedgerAsync(int settlementId, int originalLedgerId, CancellationToken cancellationToken)
        => await _db.LedgerEntries.AsNoTracking().AnyAsync(l =>
            l.SourceType == CancelSourceType
            && l.SourceId == settlementId
            && l.Description.Contains($"#{originalLedgerId}"), cancellationToken);

    private async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken)
        => _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private static SarrafSettlementDifferenceType ResolveDifferenceType(
        decimal differenceUsd,
        SarrafSettlementDifferenceTreatment treatment)
    {
        if (differenceUsd == 0m)
        {
            return SarrafSettlementDifferenceType.None;
        }

        if (treatment == SarrafSettlementDifferenceTreatment.AcceptedAmountOnly)
        {
            return differenceUsd > 0m
                ? SarrafSettlementDifferenceType.SupplierShortfall
                : SarrafSettlementDifferenceType.Gain;
        }

        return differenceUsd > 0m
            ? SarrafSettlementDifferenceType.Loss
            : SarrafSettlementDifferenceType.Gain;
    }

    private static string BuildDescription(SarrafSettlement settlement, string fallback)
        => string.IsNullOrWhiteSpace(settlement.Description)
            ? fallback
            : $"{fallback}: {settlement.Description}";

    private static void ValidatePositive(decimal value, string name)
    {
        if (value <= 0m)
        {
            throw new InvalidOperationException($"{name} must be greater than zero.");
        }
    }

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency)
            ? "USD"
            : currency.Trim().ToUpperInvariant();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Money(decimal value)
        => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal Rate(decimal value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
