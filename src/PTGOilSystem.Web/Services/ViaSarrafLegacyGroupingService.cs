using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public sealed record ViaSarrafLegacyCandidate(
    int PaymentLedgerEntryId,
    int PayableLedgerEntryId,
    int SarrafId,
    int SupplierId,
    DateTime EntryDate,
    decimal SourceAmount,
    string SourceCurrencyCode,
    decimal AmountUsd);

public sealed record ViaSarrafLegacyAmbiguity(
    int LedgerEntryId,
    string SourceType,
    int SarrafId,
    DateTime EntryDate,
    decimal SourceAmount,
    string SourceCurrencyCode,
    string Reason);

/// <summary>گزارشِ فقط‌خواندنیِ گروه‌بندیِ رکوردهای Legacy — هیچ داده‌ای تغییر نمی‌کند.</summary>
public sealed record ViaSarrafLegacyReport(
    int TotalUngroupedRows,
    int UngroupedPaymentRows,
    int UngroupedPayableRows,
    IReadOnlyList<ViaSarrafLegacyCandidate> DeterministicPairs,
    IReadOnlyList<ViaSarrafLegacyAmbiguity> Ambiguous,
    IReadOnlyList<ViaSarrafLegacyAmbiguity> Unpaired);

public sealed record ViaSarrafLegacyApplyResult(int GroupsCreated, int RowsUpdated);

public interface IViaSarrafLegacyGroupingService
{
    /// <summary>حالت Dry Run: فقط تحلیل و گزارش، بدون هیچ نوشتنی روی دیتابیس.</summary>
    Task<ViaSarrafLegacyReport> AnalyzeAsync(CancellationToken ct = default);

    /// <summary>فقط جفت‌های قطعی (دقیقاً یک تطبیقِ دوطرفه) را گروه‌بندی می‌کند. مبهم/بی‌جفت را دست نمی‌زند.</summary>
    Task<ViaSarrafLegacyApplyResult> ApplyConfirmedMatchesAsync(int? actorUserId = null, CancellationToken ct = default);

    /// <summary>تأییدِ دستیِ یک جفت مشخص توسط کاربر مجاز و ثبت یک GroupId مشترک روی هر دو ردیف.</summary>
    Task<Guid> ManualPairAsync(
        int paymentLedgerEntryId,
        int payableLedgerEntryId,
        string reason,
        int? actorUserId,
        string? actorUserName,
        CancellationToken ct = default);
}

public sealed class ViaSarrafLegacyGroupingService : IViaSarrafLegacyGroupingService
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService? _audit;

    public ViaSarrafLegacyGroupingService(ApplicationDbContext db, IAuditService? audit = null)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<ViaSarrafLegacyReport> AnalyzeAsync(CancellationToken ct = default)
    {
        var (payments, payables) = await LoadUngroupedAsync(ct);

        var deterministic = new List<ViaSarrafLegacyCandidate>();
        var ambiguous = new List<ViaSarrafLegacyAmbiguity>();
        var unpaired = new List<ViaSarrafLegacyAmbiguity>();

        foreach (var payment in payments)
        {
            var matches = payables.Where(p => IsCandidate(payment, p)).ToList();
            if (matches.Count == 0)
            {
                unpaired.Add(ToAmbiguity(payment, "هیچ ردیف بدهی صرافِ متناظری یافت نشد."));
            }
            else if (matches.Count > 1)
            {
                ambiguous.Add(ToAmbiguity(payment, $"{matches.Count} ردیف بدهی صراف با همین مبلغ/تاریخ/ارز وجود دارد."));
            }
            else
            {
                var payable = matches[0];
                // تطبیق باید دوطرفه یکتا باشد: همان payable نباید با payment دیگری هم بخورد.
                var reverse = payments.Where(pm => IsCandidate(pm, payable)).ToList();
                if (reverse.Count == 1)
                {
                    deterministic.Add(new ViaSarrafLegacyCandidate(
                        payment.Id, payable.Id, payment.SourceId, payment.SupplierId ?? 0,
                        payment.EntryDate, payment.SourceAmount ?? 0m,
                        payment.SourceCurrencyCode ?? SystemCurrency.BaseCurrencyCode, payment.AmountUsd));
                }
                else
                {
                    ambiguous.Add(ToAmbiguity(payment, $"{reverse.Count} ردیف پرداخت به همین بدهی صراف می‌خورند؛ تطبیق دوطرفه یکتا نیست."));
                }
            }
        }

        foreach (var payable in payables)
        {
            var matches = payments.Where(pm => IsCandidate(pm, payable)).ToList();
            if (matches.Count == 0)
            {
                unpaired.Add(ToAmbiguity(payable, "هیچ ردیف پرداخت تأمین‌کنندهٔ متناظری یافت نشد."));
            }
        }

        return new ViaSarrafLegacyReport(
            payments.Count + payables.Count,
            payments.Count,
            payables.Count,
            deterministic,
            ambiguous,
            unpaired);
    }

    public async Task<ViaSarrafLegacyApplyResult> ApplyConfirmedMatchesAsync(int? actorUserId = null, CancellationToken ct = default)
    {
        var report = await AnalyzeAsync(ct);
        if (report.DeterministicPairs.Count == 0)
        {
            return new ViaSarrafLegacyApplyResult(0, 0);
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var ids = report.DeterministicPairs
                .SelectMany(p => new[] { p.PaymentLedgerEntryId, p.PayableLedgerEntryId })
                .ToList();

            var entries = await _db.LedgerEntries
                .Where(l => ids.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);

            var groupsCreated = 0;
            var rowsUpdated = 0;
            var now = DateTime.UtcNow;

            foreach (var pair in report.DeterministicPairs)
            {
                if (!entries.TryGetValue(pair.PaymentLedgerEntryId, out var payment)
                    || !entries.TryGetValue(pair.PayableLedgerEntryId, out var payable))
                {
                    continue;
                }

                // محافظِ رقابتِ همزمان: اگر بین تحلیل و اعمال گروه‌بندی شده‌اند، رد شو.
                if (payment.ViaSarrafGroupId.HasValue || payable.ViaSarrafGroupId.HasValue)
                {
                    continue;
                }

                var groupId = Guid.NewGuid();
                payment.ViaSarrafGroupId = groupId;
                payable.ViaSarrafGroupId = groupId;
                payment.UpdatedAtUtc = now;
                payable.UpdatedAtUtc = now;
                payment.UpdatedByUserId = actorUserId;
                payable.UpdatedByUserId = actorUserId;
                groupsCreated++;
                rowsUpdated += 2;

                if (_audit is not null)
                {
                    var diff = $"ViaSarrafLegacyGrouping: GroupId={groupId} | Payment#{payment.Id} + Payable#{payable.Id} | GroupingMode=AutoConfirmed";
                    await _audit.LogAsync(nameof(LedgerEntry), payment.Id, AuditAction.Update, actorUserId, diff, ct);
                    await _audit.LogAsync(nameof(LedgerEntry), payable.Id, AuditAction.Update, actorUserId, diff, ct);
                }
            }

            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return new ViaSarrafLegacyApplyResult(groupsCreated, rowsUpdated);
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

    public async Task<Guid> ManualPairAsync(
        int paymentLedgerEntryId,
        int payableLedgerEntryId,
        string reason,
        int? actorUserId,
        string? actorUserName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_MANUAL_PAIR_REASON_REQUIRED",
                "دلیل تأیید دستیِ جفت الزامی است.");
        }

        if (paymentLedgerEntryId == payableLedgerEntryId)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_MANUAL_PAIR_SAME_ROW",
                "یک ردیف نمی‌تواند با خودش جفت شود.");
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var payment = await _db.LedgerEntries.FirstOrDefaultAsync(l => l.Id == paymentLedgerEntryId, ct)
                ?? throw new BusinessRuleException("VIA_SARRAF_MANUAL_PAIR_PAYMENT_NOT_FOUND", "ردیف پرداخت انتخاب‌شده یافت نشد.");
            var payable = await _db.LedgerEntries.FirstOrDefaultAsync(l => l.Id == payableLedgerEntryId, ct)
                ?? throw new BusinessRuleException("VIA_SARRAF_MANUAL_PAIR_PAYABLE_NOT_FOUND", "ردیف بدهی صراف انتخاب‌شده یافت نشد.");

            if (payment.SourceType != PaymentsController.ViaSarrafSupplierLedgerSourceType
                || payable.SourceType != PaymentsController.ViaSarrafPayableLedgerSourceType)
            {
                throw new BusinessRuleException(
                    "VIA_SARRAF_MANUAL_PAIR_SOURCE_TYPE_MISMATCH",
                    "برای جفت‌سازی، یک ردیف پرداخت تأمین‌کننده و یک ردیف بدهی صراف لازم است.");
            }

            if (payment.ViaSarrafGroupId.HasValue || payable.ViaSarrafGroupId.HasValue)
            {
                throw new BusinessRuleException(
                    "VIA_SARRAF_MANUAL_PAIR_ALREADY_GROUPED",
                    "یکی از ردیف‌ها قبلاً در یک گروه قرار گرفته است.");
            }

            if (payment.SourceId != payable.SourceId)
            {
                throw new BusinessRuleException(
                    "VIA_SARRAF_MANUAL_PAIR_SARRAF_MISMATCH",
                    "دو ردیف انتخاب‌شده به یک صراف تعلق ندارند.");
            }

            var groupId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            payment.ViaSarrafGroupId = groupId;
            payable.ViaSarrafGroupId = groupId;
            payment.UpdatedAtUtc = now;
            payable.UpdatedAtUtc = now;
            payment.UpdatedByUserId = actorUserId;
            payable.UpdatedByUserId = actorUserId;

            await _db.SaveChangesAsync(ct);

            if (_audit is not null)
            {
                var diff = $"ViaSarrafLegacyGrouping: GroupId={groupId} | Payment#{payment.Id} + Payable#{payable.Id} | " +
                           $"GroupingMode=Manual | Reason={reason.Trim()} | Actor={actorUserName ?? actorUserId?.ToString() ?? "-"}";
                await _audit.LogAsync(nameof(LedgerEntry), payment.Id, AuditAction.Update, actorUserId, diff, ct);
                await _audit.LogAsync(nameof(LedgerEntry), payable.Id, AuditAction.Update, actorUserId, diff, ct);
                await _db.SaveChangesAsync(ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return groupId;
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

    private async Task<(List<LedgerEntry> Payments, List<LedgerEntry> Payables)> LoadUngroupedAsync(CancellationToken ct)
    {
        var rows = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ViaSarrafGroupId == null
                && (l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType
                    || l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType))
            .ToListAsync(ct);

        var payments = rows.Where(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType).ToList();
        var payables = rows.Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType).ToList();
        return (payments, payables);
    }

    // معیارهای تولید نامزد — به‌تنهایی «کلید قطعی» نیستند؛ فقط برای پیشنهادِ جفت به‌کار می‌روند.
    private static bool IsCandidate(LedgerEntry payment, LedgerEntry payable)
        => payment.SourceId == payable.SourceId
            && payment.EntryDate == payable.EntryDate
            && payment.SourceAmount == payable.SourceAmount
            && payment.AmountUsd == payable.AmountUsd
            && string.Equals(payment.SourceCurrencyCode, payable.SourceCurrencyCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(payment.Reference, payable.Reference, StringComparison.OrdinalIgnoreCase);

    private static ViaSarrafLegacyAmbiguity ToAmbiguity(LedgerEntry entry, string reason)
        => new(
            entry.Id,
            entry.SourceType,
            entry.SourceId,
            entry.EntryDate,
            entry.SourceAmount ?? 0m,
            entry.SourceCurrencyCode ?? SystemCurrency.BaseCurrencyCode,
            reason);
}
