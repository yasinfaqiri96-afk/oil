using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;

namespace PartnerSettlementImport;

/// <summary>وضعیت یک ردیف نسبت به دیتابیس. مبنای idempotency.</summary>
public enum PlannedStatus
{
    New = 0,
    Exists = 1,
    Conflict = 2
}

/// <summary>یک تسویهٔ آمادهٔ ثبت — نگاشتِ ردیفِ فایل به رکوردِ PartnerSettlement.</summary>
public sealed record PlannedSettlement(
    SettlementSourceRow Source,
    string Reference,
    int FromPartnerId,
    string FromPartnerName,
    int ToPartnerId,
    string ToPartnerName,
    decimal AmountUsd,
    string Description);

public sealed record PlannedEvaluation(
    IReadOnlyDictionary<string, PlannedStatus> Statuses,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// نگاشت و ثبتِ تسویه‌های بین دو شریک از فایل مبدأ.
///
/// جهت فقط از ستونِ مبدأ می‌آید: T-Credit یعنی «پرداخت‌کنندهٔ T-Credit» به «گیرنده»، و
/// T-Debit عکسِ آن. متنِ انگلیسیِ شرح هیچ نقشی در جهت ندارد و فقط برای ردیابی نگه داشته می‌شود.
///
/// ثبت دقیقاً همان رکوردی را می‌سازد که
/// <c>PartnershipStatementController.CreateSettlement</c> می‌سازد و همان AuditLog را می‌نویسد.
/// </summary>
public static class SettlementImporter
{
    public static IReadOnlyList<PlannedSettlement> Plan(
        IReadOnlyList<SettlementSourceRow> rows,
        Partner creditPayer,
        Partner creditReceiver,
        string referencePrefix)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(creditPayer);
        ArgumentNullException.ThrowIfNull(creditReceiver);

        if (creditPayer.Id == creditReceiver.Id)
        {
            throw new InvalidOperationException("شریک پرداخت‌کننده و دریافت‌کننده نمی‌توانند یکی باشند.");
        }

        return rows
            .Select(r =>
            {
                var from = r.Column == SourceColumn.TCredit ? creditPayer : creditReceiver;
                var to = r.Column == SourceColumn.TCredit ? creditReceiver : creditPayer;
                var description = r.SourceNote is null
                    ? r.Description
                    : r.Description + " | یادداشت مبدأ: " + r.SourceNote;

                return new PlannedSettlement(
                    Source: r,
                    Reference: referencePrefix + "-R" + r.RowNumber.ToString(),
                    FromPartnerId: from.Id,
                    FromPartnerName: from.Name,
                    ToPartnerId: to.Id,
                    ToPartnerName: to.Name,
                    AmountUsd: decimal.Round(r.Amount, 4, MidpointRounding.AwayFromZero),
                    Description: Truncate(description, 1000));
            })
            .ToList();
    }

    public static async Task<PlannedEvaluation> EvaluateAsync(
        ApplicationDbContext db,
        IReadOnlyList<PlannedSettlement> planned,
        CancellationToken ct = default)
    {
        var references = planned.Select(p => p.Reference).ToList();
        var existing = await db.PartnerSettlements
            .AsNoTracking()
            .Where(s => s.Reference != null && references.Contains(s.Reference))
            .ToListAsync(ct);

        var statuses = new Dictionary<string, PlannedStatus>(StringComparer.Ordinal);
        var conflicts = new List<string>();

        foreach (var item in planned)
        {
            var match = existing.FirstOrDefault(s => s.Reference == item.Reference);
            if (match is null)
            {
                statuses[item.Reference] = PlannedStatus.New;
                continue;
            }

            var same = match.SettlementDate.Date == item.Source.SettlementDate.Date
                       && match.FromPartnerId == item.FromPartnerId
                       && match.ToPartnerId == item.ToPartnerId
                       && match.AmountUsd == item.AmountUsd;

            statuses[item.Reference] = same ? PlannedStatus.Exists : PlannedStatus.Conflict;
            if (!same)
            {
                conflicts.Add(
                    item.Reference
                    + ": db(date=" + match.SettlementDate.ToString("yyyy-MM-dd")
                    + ", from=" + match.FromPartnerId + ", to=" + match.ToPartnerId + ", usd=" + match.AmountUsd + ")"
                    + " != file(date=" + item.Source.SettlementDate.ToString("yyyy-MM-dd")
                    + ", from=" + item.FromPartnerId + ", to=" + item.ToPartnerId + ", usd=" + item.AmountUsd + ")");
            }
        }

        return new PlannedEvaluation(statuses, conflicts);
    }

    /// <summary>
    /// ثبتِ ردیف‌های NEW. هر ردیف جداگانه ثبت می‌شود تا قابل ردیابی بماند؛ هیچ تسویهٔ تجمیعی ساخته نمی‌شود.
    /// </summary>
    public static async Task<int> ApplyAsync(
        ApplicationDbContext db,
        IAuditService audit,
        IReadOnlyList<PlannedSettlement> planned,
        PlannedEvaluation evaluation,
        Action<PartnerSettlement>? onInserted = null,
        CancellationToken ct = default)
    {
        if (evaluation.Conflicts.Count > 0)
        {
            throw new InvalidOperationException("تعارض در ردیف‌های موجود؛ هیچ چیزی ثبت نشد.");
        }

        var inserted = 0;
        foreach (var item in planned)
        {
            if (evaluation.Statuses[item.Reference] != PlannedStatus.New)
            {
                continue;
            }

            // همان اعتبارسنجی PartnershipStatementController.CreateSettlement.
            if (item.FromPartnerId == item.ToPartnerId)
            {
                throw new InvalidOperationException("شریک پرداخت‌کننده و دریافت‌کننده نمی‌توانند یکی باشند.");
            }

            if (item.AmountUsd <= 0m)
            {
                throw new InvalidOperationException("مبلغ تسویه باید بزرگ‌تر از صفر باشد.");
            }

            var settlement = new PartnerSettlement
            {
                SettlementDate = item.Source.SettlementDate.Date,
                FromPartnerId = item.FromPartnerId,
                ToPartnerId = item.ToPartnerId,
                ContractId = null,
                Amount = item.AmountUsd,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = decimal.Round(item.AmountUsd * 1m, 4, MidpointRounding.AwayFromZero),
                Reference = item.Reference,
                Description = item.Description
            };

            db.PartnerSettlements.Add(settlement);
            await db.SaveChangesAsync(ct);
            await audit.LogAndSaveAsync(
                nameof(PartnerSettlement),
                settlement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("SettlementDate", settlement.SettlementDate),
                    ("FromPartnerId", settlement.FromPartnerId),
                    ("ToPartnerId", settlement.ToPartnerId),
                    ("ContractId", settlement.ContractId),
                    ("Amount", settlement.Amount),
                    ("Currency", settlement.Currency),
                    ("AmountUsd", settlement.AmountUsd),
                    ("Reference", settlement.Reference),
                    ("Description", settlement.Description)),
                ct: ct);

            onInserted?.Invoke(settlement);
            inserted++;
        }

        return inserted;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
