using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>خلاصهٔ یک گروهِ «پرداخت از طریق صراف» (جفت LedgerEntry) برای نمایش و اعتبارسنجی.</summary>
public sealed record ViaSarrafGroup(
    Guid GroupId,
    int PaymentLedgerEntryId,
    int PayableLedgerEntryId,
    int SarrafId,
    int SupplierId,
    DateTime EntryDate,
    decimal SourceAmount,
    string SourceCurrencyCode,
    decimal AmountUsd,
    int? ContractId);

public sealed record ViaSarrafContractAssignmentRequest(
    Guid GroupId,
    int? NewContractId,
    string Reason,
    int? ActorUserId,
    string? ActorUserName);

public sealed record ViaSarrafContractAssignmentResult(
    Guid GroupId,
    int? PreviousContractId,
    string? PreviousContractNumber,
    int? NewContractId,
    string? NewContractNumber);

public interface IViaSarrafContractAssignmentService
{
    /// <summary>گروه را با شناسه بارگیری و اعتبارسنجیِ ساختاری می‌کند (دو ردیفِ مکمل و سازگار).</summary>
    Task<ViaSarrafGroup> LoadGroupAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>گروهی که این LedgerEntry به آن تعلق دارد را برمی‌گرداند (اگر GroupId داشته باشد).</summary>
    Task<ViaSarrafGroup?> TryLoadGroupByLedgerEntryAsync(int ledgerEntryId, CancellationToken ct = default);

    /// <summary>
    /// قراردادِ هر دو LedgerEntریِ گروه را به یک مقدار یکسان تنظیم می‌کند (یا با null پاک می‌کند).
    /// فقط طبقه‌بندیِ قرارداد را عوض می‌کند: هیچ Ledger/Journal جدیدی نمی‌سازد و هیچ Reverse/Repost نمی‌کند.
    /// </summary>
    Task<ViaSarrafContractAssignmentResult> AssignContractAsync(
        ViaSarrafContractAssignmentRequest request,
        CancellationToken ct = default);
}

public sealed class ViaSarrafContractAssignmentService : IViaSarrafContractAssignmentService
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService? _audit;

    public ViaSarrafContractAssignmentService(ApplicationDbContext db, IAuditService? audit = null)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<ViaSarrafGroup> LoadGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        if (groupId == Guid.Empty)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_INVALID",
                "شناسهٔ گروه پرداخت صرافی معتبر نیست.");
        }

        var entries = await _db.LedgerEntries
            .Where(l => l.ViaSarrafGroupId == groupId)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        return BuildValidatedGroup(groupId, entries);
    }

    public async Task<ViaSarrafGroup?> TryLoadGroupByLedgerEntryAsync(int ledgerEntryId, CancellationToken ct = default)
    {
        var groupId = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.Id == ledgerEntryId)
            .Select(l => l.ViaSarrafGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is null || groupId == Guid.Empty)
        {
            return null;
        }

        return await LoadGroupAsync(groupId.Value, ct);
    }

    public async Task<ViaSarrafContractAssignmentResult> AssignContractAsync(
        ViaSarrafContractAssignmentRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_CONTRACT_REASON_REQUIRED",
                "دلیل ثبت یا تغییر قرارداد الزامی است.");
        }

        // ردیف‌ها را tracked بارگیری کن تا در همین transaction به‌روزرسانی شوند.
        var entries = await _db.LedgerEntries
            .Where(l => l.ViaSarrafGroupId == request.GroupId)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        var group = BuildValidatedGroup(request.GroupId, entries);

        var previousContractId = group.ContractId;
        string? previousContractNumber = previousContractId.HasValue
            ? await _db.Contracts.AsNoTracking()
                .Where(c => c.Id == previousContractId.Value)
                .Select(c => c.ContractNumber)
                .FirstOrDefaultAsync(ct)
            : null;

        Contract? newContract = null;
        if (request.NewContractId.HasValue)
        {
            newContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == request.NewContractId.Value, ct)
                ?? throw new BusinessRuleException(
                    "VIA_SARRAF_CONTRACT_NOT_FOUND",
                    "قرارداد انتخاب‌شده معتبر نیست.");

            if (newContract.ContractType != ContractType.Purchase)
            {
                throw new BusinessRuleException(
                    "VIA_SARRAF_CONTRACT_NOT_PURCHASE",
                    "فقط قرارداد خرید قابل انتخاب است.");
            }

            if (newContract.SupplierId != group.SupplierId)
            {
                throw new BusinessRuleException(
                    "VIA_SARRAF_CONTRACT_SUPPLIER_MISMATCH",
                    "قرارداد باید متعلق به همان تأمین‌کنندهٔ این پرداخت باشد.");
            }

            // مرزِ شرکت: تأمین‌کننده و LedgerEntry شناسهٔ شرکت ندارند، پس تنها لنگرِ شرکت خودِ قرارداد است.
            // هنگام «تغییر قرارداد»، قرارداد جدید باید در همان شرکتِ قرارداد قبلی باشد تا طبقه‌بندی بین شرکت‌ها جابه‌جا نشود.
            if (previousContractId.HasValue)
            {
                var previousCompanyId = await _db.Contracts.AsNoTracking()
                    .Where(c => c.Id == previousContractId.Value)
                    .Select(c => (int?)c.CompanyId)
                    .FirstOrDefaultAsync(ct);
                if (previousCompanyId.HasValue && previousCompanyId.Value != newContract.CompanyId)
                {
                    throw new BusinessRuleException(
                        "VIA_SARRAF_CONTRACT_COMPANY_MISMATCH",
                        "قرارداد جدید باید متعلق به همان شرکتِ قرارداد قبلی باشد.");
                }
            }
        }

        if (previousContractId == request.NewContractId)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_CONTRACT_UNCHANGED",
                "قرارداد انتخاب‌شده با قرارداد فعلی یکسان است؛ تغییری اعمال نشد.");
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var entry in entries)
            {
                entry.ContractId = request.NewContractId;
                entry.UpdatedAtUtc = now;
                entry.UpdatedByUserId = request.ActorUserId;
            }

            await _db.SaveChangesAsync(ct);

            if (_audit is not null)
            {
                // دیفِ صریح تا همهٔ فیلدهای الزامیِ Audit (گروه، قبل/بعدِ قرارداد، دلیل، کاربر) ثبت شوند؛
                // ForUpdate فیلدهای بدون‌تغییر را حذف می‌کند و GroupId/Mode را می‌انداخت.
                var diff =
                    $"ViaSarrafContractAssignment: GroupId={request.GroupId} | " +
                    $"ContractId {FormatContract(previousContractId)} → {FormatContract(request.NewContractId)} | " +
                    $"GroupingMode=Manual | Reason={request.Reason.Trim()} | " +
                    $"Actor={request.ActorUserName ?? request.ActorUserId?.ToString() ?? "-"}";
                foreach (var entry in entries)
                {
                    await _audit.LogAsync(
                        nameof(LedgerEntry),
                        entry.Id,
                        AuditAction.Update,
                        request.ActorUserId,
                        diff: diff,
                        ct);
                }

                await _db.SaveChangesAsync(ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }

        return new ViaSarrafContractAssignmentResult(
            request.GroupId,
            previousContractId,
            previousContractNumber,
            request.NewContractId,
            newContract?.ContractNumber);
    }

    private static string FormatContract(int? contractId)
        => contractId?.ToString() ?? "(بدون قرارداد)";

    /// <summary>
    /// ساختارِ گروه را قطعی می‌کند: دقیقاً دو ردیف، یکی Payment و یکی Payable، با صراف/مبلغ/ارز/تاریخ سازگار.
    /// اگر گروه ناقص یا ناسازگار باشد، عملیات متوقف می‌شود.
    /// </summary>
    private static ViaSarrafGroup BuildValidatedGroup(Guid groupId, List<LedgerEntry> entries)
    {
        if (entries.Count == 0)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_NOT_FOUND",
                "هیچ ردیفی برای این گروه پرداخت صرافی یافت نشد.");
        }

        if (entries.Count != 2)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_MALFORMED",
                $"گروه باید دقیقاً دو ردیف داشته باشد اما {entries.Count} ردیف دارد.");
        }

        var paymentEntry = entries.SingleOrDefault(e => e.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        var payableEntry = entries.SingleOrDefault(e => e.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);

        if (paymentEntry is null || payableEntry is null)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_SOURCE_TYPE_MISMATCH",
                "گروه باید یک ردیف پرداخت تأمین‌کننده و یک ردیف بدهی صراف داشته باشد.");
        }

        if (paymentEntry.SourceId != payableEntry.SourceId)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_SARRAF_MISMATCH",
                "دو ردیف گروه به یک صراف تعلق ندارند.");
        }

        if (!paymentEntry.SupplierId.HasValue)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_SUPPLIER_MISSING",
                "ردیف پرداخت تأمین‌کننده در این گروه، تأمین‌کننده ندارد.");
        }

        if (paymentEntry.ContractId != payableEntry.ContractId)
        {
            throw new BusinessRuleException(
                "VIA_SARRAF_GROUP_CONTRACT_DIVERGED",
                "قرارداد دو ردیف گروه با هم یکسان نیست؛ ابتدا باید هماهنگ شوند.");
        }

        return new ViaSarrafGroup(
            groupId,
            paymentEntry.Id,
            payableEntry.Id,
            paymentEntry.SourceId,
            paymentEntry.SupplierId.Value,
            paymentEntry.EntryDate,
            paymentEntry.SourceAmount ?? 0m,
            paymentEntry.SourceCurrencyCode ?? SystemCurrency.BaseCurrencyCode,
            paymentEntry.AmountUsd,
            paymentEntry.ContractId);
    }
}
