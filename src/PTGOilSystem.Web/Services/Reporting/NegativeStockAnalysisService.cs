using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// Whether a negative stock scope is still negative now, or only dipped below zero
/// somewhere in the middle of its timeline and recovered later.
/// </summary>
public enum NegativeStockStatus
{
    /// <summary>Closing balance is still below zero — a real open shortage.</summary>
    Open = 1,

    /// <summary>The dip healed later; typically a backdated entry ordering artefact.</summary>
    HealedLegacy = 2
}

public sealed record NegativeStockFinding(
    int ProductId,
    string ProductName,
    int? CompanyId,
    string CompanyName,
    int? ContractId,
    string ContractNumber,
    int TerminalId,
    string TerminalName,
    int? StorageTankId,
    string StorageTankCode,
    DateTime FirstNegativeDate,
    decimal FirstNegativeBalanceMt,
    decimal ClosingBalanceMt,
    int CausingMovementId,
    string CausingMovementReference,
    int? CausingSalesTransactionId,
    string ProbableCause,
    NegativeStockStatus Status);

public interface INegativeStockAnalysisService
{
    /// <summary>
    /// Walks every stock scope (product + contract + terminal + tank) that has at
    /// least one outgoing movement and reports the first point where the running
    /// balance went below zero. Read-only: nothing is repaired or backfilled.
    /// </summary>
    Task<IReadOnlyList<NegativeStockFinding>> AnalyzeAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);
}

public sealed class NegativeStockAnalysisService : INegativeStockAnalysisService
{
    private const string Unassigned = "—";

    private readonly ApplicationDbContext _db;

    public NegativeStockAnalysisService(ApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<NegativeStockFinding>> AnalyzeAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.InventoryMovements.AsNoTracking().AsQueryable();
        if (filter.ProductId.HasValue) query = query.Where(m => m.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) query = query.Where(m => m.ContractId == filter.ContractId.Value);
        if (filter.TerminalId.HasValue) query = query.Where(m => m.TerminalId == filter.TerminalId.Value);
        if (filter.StorageTankId.HasValue) query = query.Where(m => m.StorageTankId == filter.StorageTankId.Value);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(m => m.MovementDate < end);
        }

        // فقط scopeهایی که دستِ‌کم یک خروج دارند می‌توانند منفی شوند؛ بقیه از همان SQL
        // حذف می‌شوند تا کل جدول به حافظه نیاید.
        var rows = await query
            .Where(m => _db.InventoryMovements.Any(x =>
                x.ProductId == m.ProductId
                && x.ContractId == m.ContractId
                && x.TerminalId == m.TerminalId
                && x.StorageTankId == m.StorageTankId
                && (x.Direction == MovementDirection.Out || x.Direction == MovementDirection.Transfer)))
            .OrderBy(m => m.ProductId)
            .ThenBy(m => m.ContractId)
            .ThenBy(m => m.TerminalId)
            .ThenBy(m => m.StorageTankId)
            .ThenBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .Select(m => new MovementRow(
                m.Id,
                m.ProductId,
                m.Product != null ? m.Product.Name : "",
                m.ContractId,
                m.Contract != null ? m.Contract.ContractNumber : null,
                m.Contract != null ? (int?)m.Contract.CompanyId : null,
                m.Contract != null && m.Contract.Company != null ? m.Contract.Company.Name : null,
                m.TerminalId,
                m.Terminal != null ? m.Terminal.Name : "",
                m.StorageTankId,
                m.StorageTank != null ? m.StorageTank.TankCode : null,
                m.MovementDate,
                m.Direction,
                m.QuantityMt,
                m.ReferenceDocument,
                m.SalesTransactionId))
            .ToListAsync(ct);

        var findings = new List<NegativeStockFinding>();
        foreach (var scope in rows.GroupBy(r => new { r.ProductId, r.ContractId, r.TerminalId, r.StorageTankId }))
        {
            var ordered = scope.ToList();
            decimal running = 0m;
            MovementRow? culprit = null;
            decimal firstNegativeBalance = 0m;

            foreach (var row in ordered)
            {
                running += Signed(row.Direction, row.QuantityMt);
                if (running < 0m && culprit is null)
                {
                    culprit = row;
                    firstNegativeBalance = running;
                }
            }

            if (culprit is null)
            {
                continue;
            }

            var closing = ordered.Sum(r => Signed(r.Direction, r.QuantityMt));
            findings.Add(new NegativeStockFinding(
                ProductId: culprit.ProductId,
                ProductName: culprit.ProductName,
                CompanyId: culprit.CompanyId,
                CompanyName: culprit.CompanyName ?? Unassigned,
                ContractId: culprit.ContractId,
                ContractNumber: culprit.ContractNumber ?? Unassigned,
                TerminalId: culprit.TerminalId,
                TerminalName: culprit.TerminalName,
                StorageTankId: culprit.StorageTankId,
                StorageTankCode: culprit.StorageTankCode ?? Unassigned,
                FirstNegativeDate: culprit.MovementDate,
                FirstNegativeBalanceMt: Round(firstNegativeBalance),
                ClosingBalanceMt: Round(closing),
                CausingMovementId: culprit.Id,
                CausingMovementReference: culprit.ReferenceDocument ?? Unassigned,
                CausingSalesTransactionId: culprit.SalesTransactionId,
                ProbableCause: DescribeCause(ordered, culprit),
                Status: closing < 0m ? NegativeStockStatus.Open : NegativeStockStatus.HealedLegacy));
        }

        return findings
            .OrderBy(f => f.Status)
            .ThenBy(f => f.FirstNegativeDate)
            .ToList();
    }

    /// <summary>
    /// Honest, evidence-based cause. Nothing here repairs data; it only says which of
    /// the three observable patterns the timeline matches.
    /// </summary>
    private static string DescribeCause(IReadOnlyList<MovementRow> ordered, MovementRow culprit)
    {
        var hasAnyInbound = ordered.Any(r =>
            r.Direction == MovementDirection.In || r.Direction == MovementDirection.Adjustment);
        if (!hasAnyInbound)
        {
            return "هیچ ورودی‌ای برای این ترکیب قرارداد/ترمینال/مخزن ثبت نشده — احتمالاً scope خروج اشتباه است.";
        }

        var laterInboundMt = ordered
            .Where(r => (r.MovementDate > culprit.MovementDate
                    || (r.MovementDate == culprit.MovementDate && r.Id > culprit.Id))
                && (r.Direction == MovementDirection.In || r.Direction == MovementDirection.Adjustment))
            .Sum(r => r.QuantityMt);
        if (laterInboundMt > 0m)
        {
            return $"ورودی {laterInboundMt:N4} MT با تاریخ بعد از این خروج ثبت شده — ترتیب تاریخِ ثبت (Legacy backdating).";
        }

        return culprit.SalesTransactionId.HasValue
            ? "خروج فروش بدون موجودی کافی در همان تاریخ — کسری واقعی."
            : "خروج بدون موجودی کافی در همان تاریخ — کسری واقعی.";
    }

    private static decimal Signed(MovementDirection direction, decimal quantityMt) => direction switch
    {
        MovementDirection.In => quantityMt,
        MovementDirection.Adjustment => quantityMt,
        MovementDirection.Out => -quantityMt,
        MovementDirection.Transfer => -quantityMt,
        _ => 0m
    };

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record MovementRow(
        int Id,
        int ProductId,
        string ProductName,
        int? ContractId,
        string? ContractNumber,
        int? CompanyId,
        string? CompanyName,
        int TerminalId,
        string TerminalName,
        int? StorageTankId,
        string? StorageTankCode,
        DateTime MovementDate,
        MovementDirection Direction,
        decimal QuantityMt,
        string? ReferenceDocument,
        int? SalesTransactionId);
}
