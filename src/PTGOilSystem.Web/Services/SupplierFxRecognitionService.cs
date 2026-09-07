using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// ثبتِ تفاوت نرخِ حساب ارزی تأمین‌کننده در دفتر.
///
/// <see cref="ISupplierFxSettlementService"/> فقط حساب می‌کند؛ این سرویس همان عدد را ثبت
/// می‌کند تا مانده حساب، «پیش‌پرداخت آزاد»، گزارش بیلانس و سود و زیان همگی خودشان درست
/// شوند — بدون تغییر در هیچ‌کدام از آن مسیرها.
///
/// دو سطرِ متوازن ثبت می‌شود (همان الگوی تفاوت نرخ حواله صراف):
///   • ضرر → بستانکارِ تأمین‌کننده (بدهی بالا می‌رود) + مصرفِ «تفاوت نرخ ارز» با سطر بدهکار
///   • سود → بدهکارِ تأمین‌کننده (بدهی پایین می‌آید) + سطر بستانکارِ درآمد
///
/// سطرهای ثبت‌شده عمداً <c>SourceCurrencyCode = USD</c> دارند، پس در محاسبهٔ بعدیِ همان
/// موتور شرکت نمی‌کنند و اجرای دوباره عددِ جدید نمی‌سازد. برای اطمینان، هر اجرا اول ثبتِ
/// قبلیِ همین تأمین‌کننده را برمی‌دارد و بعد وضعیت فعلی را می‌نویسد.
/// </summary>
public interface ISupplierFxRecognitionService
{
    Task<SupplierFxRecognitionResult> RecognizeAsync(
        int supplierId,
        string? actorUserName = null,
        CancellationToken ct = default);

    /// <summary>برداشتنِ ثبتِ تفاوت نرخِ همین تأمین‌کننده. مانده به حالت پیش از شناسایی برمی‌گردد.</summary>
    Task<int> RemoveAsync(int supplierId, CancellationToken ct = default);
}

/// <param name="RecognizedUsd">مبلغ ثبت‌شده. مثبت = ضرر، منفی = سود، صفر = چیزی برای ثبت نبود.</param>
/// <param name="RemovedEntries">تعداد سطرهای ثبتِ قبلی که برداشته شد.</param>
/// <param name="ExpenseTransactionId">مصرفِ ساخته‌شده در حالت ضرر.</param>
public sealed record SupplierFxRecognitionResult(
    int SupplierId,
    decimal RecognizedUsd,
    int RemovedEntries,
    int? ExpenseTransactionId)
{
    public bool PostedAnything => RecognizedUsd != 0m;
}

public sealed class SupplierFxRecognitionService : ISupplierFxRecognitionService
{
    /// <summary>سطرِ تعدیلِ حساب تأمین‌کننده — تنها سطری که مانده او را جابه‌جا می‌کند.</summary>
    public const string PartyLedgerSourceType = "SupplierFxDifference";

    /// <summary>سطر درآمدِ سود تفاوت نرخ. سیستم موجودیتِ درآمد ندارد، پس سود سطرِ دفتری می‌شود.</summary>
    public const string GainLedgerSourceType = "SupplierFxGain";

    public const string FxDifferenceExpenseCode = "FX_DIFFERENCE";
    private const string FxRateSource = "Supplier FX settlement difference";

    private readonly ApplicationDbContext _db;
    private readonly ISupplierFxSettlementService _settlements;
    private readonly IAuditService? _audit;
    private readonly IExpenseSettlementValidator _settlementValidator;
    private readonly ILedgerPostingService _ledger;

    public SupplierFxRecognitionService(
        ApplicationDbContext db,
        ISupplierFxSettlementService? settlements = null,
        IAuditService? audit = null,
        IExpenseSettlementValidator? settlementValidator = null)
    {
        _db = db;
        _settlements = settlements ?? new SupplierFxSettlementService(db);
        _audit = audit;
        _settlementValidator = settlementValidator ?? new ExpenseSettlementValidator();
        _ledger = new LedgerPostingService(db);
    }

    /// <summary>نشانهٔ ثبت‌های همین تأمین‌کننده، برای اجرای دوباره و برداشتن.</summary>
    private static string BuildMarker(int supplierId) => $"[SFX-{supplierId}]";

    public async Task<SupplierFxRecognitionResult> RecognizeAsync(
        int supplierId,
        string? actorUserName = null,
        CancellationToken ct = default)
    {
        // اول وضعیتِ پیش از شناسایی سنجیده می‌شود: سطرهای ثبتِ قبلی دالری‌اند و در محاسبه
        // شرکت نمی‌کنند، ولی برداشتنشان قبل از محاسبه، نتیجه را از هر ترتیبی مستقل می‌کند.
        var removed = await RemoveAsync(supplierId, ct);

        var settlement = await _settlements.GetAsync(supplierId, ct);
        var differenceUsd = decimal.Round(settlement.RealizedFxDifferenceUsd, 4, MidpointRounding.AwayFromZero);

        // مبالغ دالریِ ثبت‌شده گرد شده‌اند، پس حتی وقتی نرخ دو طرف یکی است، تفاوت می‌تواند
        // چند ده‌هزارمِ دالر شود. زیر یک سنت، سند ساختن معنی ندارد.
        if (Math.Abs(differenceUsd) < 0.01m || settlement.RecognitionDate is not DateTime recognitionDate)
        {
            if (removed > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            return new SupplierFxRecognitionResult(supplierId, 0m, removed, null);
        }

        var amountUsd = Math.Abs(differenceUsd);
        var isLoss = differenceUsd > 0m;
        var marker = BuildMarker(supplierId);
        var headline = isLoss ? "ضرر تفاوت نرخ ارز حساب تأمین‌کننده" : "سود تفاوت نرخ ارز حساب تأمین‌کننده";
        var currencies = string.Join("، ", settlement.Currencies.Select(c => c.CurrencyCode));
        var description = $"{headline} {marker} — تسویهٔ بدهی {currencies} با نرخِ متفاوت از نرخ ثبت بدهی";

        int? expenseId = null;
        if (isLoss)
        {
            var fxType = await EnsureFxDifferenceExpenseTypeAsync(ct);
            var expense = new ExpenseTransaction
            {
                ExpenseTypeId = fxType.Id,
                // بدون قرارداد: تفاوت نرخ به یک قرارداد خاص تعلق ندارد و بستنش به قرارداد،
                // بهای تمام‌شدهٔ همان قرارداد را دوباره‌شماری می‌کند. ردیابی در Description است.
                ContractId = null,
                ExpenseDate = recognitionDate.Date,
                Amount = amountUsd,
                Currency = SystemCurrency.BaseCurrencyCode,
                AppliedFxRateToUsd = 1m,
                AmountUsd = amountUsd,
                Description = description,
                // تعدیل داخلی: نه پول نقدی جابه‌جا شده، نه طرف‌حسابِ بیرونی دارد.
                SettlementMode = ExpenseSettlementMode.NonCash
            };
            _settlementValidator.Validate(expense);
            _db.ExpenseTransactions.Add(expense);
            await _db.SaveChangesAsync(ct);
            expenseId = expense.Id;
        }

        // سمت سود/زیان — بدون طرف‌حساب، تا مانده تأمین‌کننده را جابه‌جا نکند.
        _ledger.Post(BuildLedger(
            recognitionDate,
            isLoss ? LedgerSide.Debit : LedgerSide.Credit,
            amountUsd,
            description,
            sourceType: isLoss ? "Expense" : GainLedgerSourceType,
            sourceId: isLoss ? expenseId!.Value : supplierId,
            supplierId: null));

        // سمت حساب تأمین‌کننده — همان سطری که مانده را از «طلب موهوم» به بدهیِ واقعی می‌برد.
        var partyEntry = _ledger.Post(BuildLedger(
            recognitionDate,
            isLoss ? LedgerSide.Credit : LedgerSide.Debit,
            amountUsd,
            description,
            sourceType: PartyLedgerSourceType,
            sourceId: supplierId,
            supplierId: supplierId));

        await _db.SaveChangesAsync(ct);

        if (_audit is not null)
        {
            await _audit.LogAsync(
                nameof(LedgerEntry),
                partyEntry.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("Type", isLoss ? "SupplierFxLoss" : "SupplierFxGain"),
                    ("SupplierId", supplierId),
                    ("AmountUsd", amountUsd),
                    ("Side", partyEntry.Side),
                    ("ExpenseTransactionId", expenseId),
                    ("RemovedPreviousEntries", removed),
                    ("Actor", actorUserName)));
        }

        return new SupplierFxRecognitionResult(supplierId, differenceUsd, removed, expenseId);
    }

    public async Task<int> RemoveAsync(int supplierId, CancellationToken ct = default)
    {
        var marker = BuildMarker(supplierId);
        var removed = 0;

        var expenses = await _db.ExpenseTransactions
            .Where(e => e.Description != null && e.Description.Contains(marker))
            .ToListAsync(ct);
        if (expenses.Count > 0)
        {
            var expenseIds = expenses.Select(e => e.Id).ToList();
            var expenseLedgers = await _db.LedgerEntries
                .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
                .ToListAsync(ct);
            _db.LedgerEntries.RemoveRange(expenseLedgers);
            _db.ExpenseTransactions.RemoveRange(expenses);
            removed += expenseLedgers.Count + expenses.Count;
        }

        var ownLedgers = await _db.LedgerEntries
            .Where(l => (l.SourceType == PartyLedgerSourceType || l.SourceType == GainLedgerSourceType)
                && l.Description.Contains(marker))
            .ToListAsync(ct);
        if (ownLedgers.Count > 0)
        {
            _db.LedgerEntries.RemoveRange(ownLedgers);
            removed += ownLedgers.Count;
        }

        return removed;
    }

    private async Task<ExpenseType> EnsureFxDifferenceExpenseTypeAsync(CancellationToken ct)
    {
        var existing = await _db.ExpenseTypes
            .FirstOrDefaultAsync(e => e.IsActive
                && (e.Category == "FxDifference" || e.Code == FxDifferenceExpenseCode), ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ExpenseType
        {
            Code = FxDifferenceExpenseCode,
            Name = "FX Difference",
            NamePersian = "تفاوت نرخ ارز",
            Category = "FxDifference",
            IsActive = true
        };
        _db.ExpenseTypes.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    // سطرِ شناسایی همیشه دالری است. این عمدی است: موتور محاسبه فقط سطرهای ارزِ غیرپایه را
    // می‌خواند، پس ثبتِ شناسایی هرگز خودش را دوباره وارد محاسبه نمی‌کند.
    private static LedgerPostingRequest BuildLedger(
        DateTime entryDate,
        LedgerSide side,
        decimal amountUsd,
        string description,
        string sourceType,
        int sourceId,
        int? supplierId)
        => new()
        {
            EntryDate = entryDate.Date,
            Side = side,
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amountUsd,
            SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = entryDate.Date,
            AppliedFxRateSource = FxRateSource,
            Description = description,
            SourceType = sourceType,
            SourceId = sourceId,
            ContractId = null,
            SupplierId = supplierId
        };
}
