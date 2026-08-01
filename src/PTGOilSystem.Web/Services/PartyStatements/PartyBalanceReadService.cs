using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Services.PartyStatements;

public interface IPartyBalanceReadService
{
    Task<IReadOnlyList<PartyBalanceSnapshot>> GetBalancesAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default);
}

public sealed record PartyBalanceSnapshot(
    PartyStatementPartyType PartyType,
    int PartyId,
    string PartyName,
    decimal OpeningBalanceUsd,
    decimal TotalReceiptUsd,
    decimal TotalOutflowUsd,
    decimal PeriodMovementUsd,
    decimal ClosingBalanceUsd,
    DateTime? LastEntryDate,
    string BalanceMeaning,
    string? DetailsController);

/// <summary>
/// Bulk balance reader used by management balance reports. It shares the same
/// direction resolver, party policies and closing formula as the official party
/// statement, while keeping query count bounded instead of opening one statement
/// per party.
/// </summary>
public sealed class PartyBalanceReadService : IPartyBalanceReadService
{
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementPolicyResolver _policies;
    private readonly ICompanyFlowDirectionResolver _directions;
    private readonly ICompanyFlowBalanceService _balances;

    public PartyBalanceReadService(
        ApplicationDbContext db,
        IPartyStatementPolicyResolver policies,
        ICompanyFlowDirectionResolver directions,
        ICompanyFlowBalanceService balances)
    {
        _db = db;
        _policies = policies;
        _directions = directions;
        _balances = balances;
    }

    public async Task<IReadOnlyList<PartyBalanceSnapshot>> GetBalancesAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var events = new List<BalanceEvent>();
        await AddLedgerEventsAsync(events, filter, ct);

        if (!filter.CustomerId.HasValue && !filter.SupplierId.HasValue)
        {
            await AddSarrafEventsAsync(events, filter, ct);
            await AddEmployeeEventsAsync(events, filter, ct);
            await AddPartnerEventsAsync(events, filter, ct);
        }

        var names = await LoadPartyNamesAsync(events, ct);
        var from = filter.FromDate?.Date;

        return events
            .GroupBy(e => new { e.PartyType, e.PartyId })
            .Select(group =>
            {
                var openingRows = from.HasValue
                    ? group.Where(e => e.Date < from.Value)
                    : [];
                var periodRows = from.HasValue
                    ? group.Where(e => e.Date >= from.Value)
                    : group;

                var opening = openingRows.Sum(e => _balances.SignedEffect(
                    e.Direction,
                    e.AmountUsd,
                    CompanyFlowAccountKind.PartyAccount));
                var summary = _balances.Summarize(
                    opening,
                    periodRows.Select(e => new CompanyFlowAmount(e.Direction, e.AmountUsd)),
                    CompanyFlowAccountKind.PartyAccount);
                var policy = _policies.Resolve(group.Key.PartyType);

                return new PartyBalanceSnapshot(
                    group.Key.PartyType,
                    group.Key.PartyId,
                    names.GetValueOrDefault((group.Key.PartyType, group.Key.PartyId), "-"),
                    summary.OpeningBalance,
                    summary.TotalReceipt,
                    summary.TotalOutflow,
                    summary.NetMovement,
                    summary.ClosingBalance,
                    group.Max(e => (DateTime?)e.Date),
                    policy.BalanceMeaning(summary.ClosingBalance, isEnglish: false),
                    DetailsController(group.Key.PartyType));
            })
            .Where(row => row.OpeningBalanceUsd != 0m
                || row.TotalReceiptUsd != 0m
                || row.TotalOutflowUsd != 0m)
            .OrderByDescending(row => Math.Abs(row.ClosingBalanceUsd))
            .ThenBy(row => row.PartyName)
            .ToList();
    }

    private async Task AddLedgerEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var query = _db.LedgerEntries.AsNoTracking()
            .WhereEffectiveSarrafSettlementLegs(_db);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }
        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            query = query.Where(l => l.ContractId == contractId
                || (l.SourceType == CompanyFlowSourceTypes.Sale
                    && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.ContractId == contractId)));
        }

        var rows = await query
            .Select(l => new
            {
                l.EntryDate,
                l.Side,
                l.AmountUsd,
                l.SourceType,
                l.SourceId,
                l.CustomerId,
                EffectiveCustomerId = l.CustomerId
                    ?? (l.SupplierId == null
                        && l.ServiceProviderId == null
                        && l.DriverId == null
                        && l.EmployeeId == null
                        && l.Contract != null
                            ? l.Contract.CustomerId
                            : null)
                    ?? (l.SourceType == CompanyFlowSourceTypes.Sale
                        ? _db.SalesTransactions
                            .Where(s => s.Id == l.SourceId)
                            .Select(s => (int?)s.CustomerId)
                            .FirstOrDefault()
                        : null),
                EffectiveSupplierId = l.SourceType == LedgerEntryOwnership.ViaSarrafPayableSourceType
                    ? null
                    : l.SupplierId
                        ?? (l.SupplierId == null
                            && l.ServiceProviderId == null
                            && l.DriverId == null
                            && l.CustomerId == null
                            && l.EmployeeId == null
                            && l.Contract != null
                            && l.Contract.ContractType == ContractType.Purchase
                                ? l.Contract.SupplierId
                                : null),
                l.ServiceProviderId,
                l.DriverId,
                EffectiveCompanyId = l.Contract != null
                    ? (int?)l.Contract.CompanyId
                    : l.SourceType == CompanyFlowSourceTypes.Sale
                        ? _db.SalesTransactions
                            .Where(s => s.Id == l.SourceId)
                            .Select(s => s.CompanyId)
                            .FirstOrDefault()
                        : null
            })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.EffectiveCustomerId.HasValue
                && (!filter.CustomerId.HasValue || row.EffectiveCustomerId == filter.CustomerId.Value)
                && !filter.SupplierId.HasValue)
            {
                AddLedgerEvent(target, row.EffectiveCustomerId.Value, PartyStatementPartyType.Customer,
                    CompanyFlowPartyRole.Customer, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
            }

            if (row.EffectiveSupplierId.HasValue
                && (!filter.SupplierId.HasValue || row.EffectiveSupplierId == filter.SupplierId.Value)
                && !filter.CustomerId.HasValue)
            {
                AddLedgerEvent(target, row.EffectiveSupplierId.Value, PartyStatementPartyType.Supplier,
                    CompanyFlowPartyRole.Supplier, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
            }

            if (!filter.CustomerId.HasValue && !filter.SupplierId.HasValue)
            {
                if (row.ServiceProviderId.HasValue)
                {
                    AddLedgerEvent(target, row.ServiceProviderId.Value, PartyStatementPartyType.ServiceProvider,
                        CompanyFlowPartyRole.ServiceProvider, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
                }
                if (row.DriverId.HasValue)
                {
                    AddLedgerEvent(target, row.DriverId.Value, PartyStatementPartyType.Driver,
                        CompanyFlowPartyRole.Driver, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
                }
                if (row.EffectiveCompanyId.HasValue)
                {
                    AddLedgerEvent(target, row.EffectiveCompanyId.Value, PartyStatementPartyType.Company,
                        CompanyFlowPartyRole.Company, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
                }
            }
        }
    }

    private void AddLedgerEvent(
        List<BalanceEvent> target,
        int partyId,
        PartyStatementPartyType partyType,
        CompanyFlowPartyRole role,
        DateTime date,
        LedgerSide side,
        decimal amountUsd,
        string sourceType)
    {
        var lifecycle = CompanyFlowSourceTypes.IsReversal(sourceType)
            ? CompanyFlowLifecycle.Reversal
            : CompanyFlowLifecycle.Original;
        var direction = _directions.Resolve(new CompanyFlowEvent(sourceType, side, role, lifecycle));
        target.Add(new BalanceEvent(partyType, partyId, date, direction, Math.Abs(amountUsd)));
    }

    private async Task AddSarrafEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var payments = _db.PaymentTransactions.AsNoTracking().Where(p => p.SarrafId.HasValue);
        var settlements = _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted);
        var via = _db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == LedgerEntryOwnership.ViaSarrafPayableSourceType);

        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            payments = payments.Where(p => p.PaymentDate < end);
            settlements = settlements.Where(s => s.SettlementDate < end);
            via = via.Where(l => l.EntryDate < end);
        }
        if (filter.ContractId.HasValue)
        {
            payments = payments.Where(p => p.ContractId == filter.ContractId.Value);
            settlements = settlements.Where(s => s.ContractId == filter.ContractId.Value);
            via = via.Where(l => l.ContractId == filter.ContractId.Value);
        }

        var paymentRows = await payments
            .Select(p => new { SarrafId = p.SarrafId!.Value, p.PaymentDate, p.Direction, p.AmountUsd })
            .ToListAsync(ct);
        target.AddRange(paymentRows.Select(p => new BalanceEvent(
            PartyStatementPartyType.Sarraf,
            p.SarrafId,
            p.PaymentDate,
            p.Direction == PaymentDirection.Out
                ? CompanyFlowDirection.Outflow
                : CompanyFlowDirection.Receipt,
            Math.Abs(p.AmountUsd))));

        var settlementRows = await settlements
            .Select(s => new { s.SarrafId, s.SettlementDate, s.Direction, s.SarrafChargedAmountUsd })
            .ToListAsync(ct);
        target.AddRange(settlementRows.Select(s => new BalanceEvent(
            PartyStatementPartyType.Sarraf,
            s.SarrafId,
            s.SettlementDate,
            s.Direction == SarrafSettlementDirection.Out
                ? CompanyFlowDirection.Receipt
                : CompanyFlowDirection.Outflow,
            Math.Abs(s.SarrafChargedAmountUsd))));

        var viaRows = await via
            .Select(l => new { SarrafId = l.SourceId, l.EntryDate, l.Side, l.AmountUsd, l.SourceType })
            .ToListAsync(ct);
        foreach (var row in viaRows)
        {
            AddLedgerEvent(target, row.SarrafId, PartyStatementPartyType.Sarraf,
                CompanyFlowPartyRole.Sarraf, row.EntryDate, row.Side, row.AmountUsd, row.SourceType);
        }
    }

    private async Task AddEmployeeEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var query = _db.EmployeeSalaryTransactions.AsNoTracking().Where(t => !t.IsCancelled);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(t => t.TransactionDate < end);
        }

        var rows = await query
            .Select(t => new { t.EmployeeId, t.TransactionDate, t.TransactionType, t.AmountUsd })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var side = row.AmountUsd >= 0m ? LedgerSide.Credit : LedgerSide.Debit;
            var direction = _directions.Resolve(new CompanyFlowEvent(
                row.TransactionType.ToString(),
                side,
                CompanyFlowPartyRole.Employee));
            target.Add(new BalanceEvent(
                PartyStatementPartyType.Employee,
                row.EmployeeId,
                row.TransactionDate,
                direction,
                Math.Abs(row.AmountUsd)));
        }
    }

    private async Task AddPartnerEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var shares = _db.ContractPartners.AsNoTracking().AsQueryable();
        if (filter.ContractId.HasValue)
        {
            shares = shares.Where(s => s.ContractId == filter.ContractId.Value);
        }
        var shareRows = await shares
            .Select(s => new { s.ContractId, s.PartnerId, s.SharePercent })
            .ToListAsync(ct);
        if (shareRows.Count == 0)
        {
            return;
        }

        var contractIds = shareRows.Select(s => s.ContractId).Distinct().ToArray();
        var saleMap = await _db.SalesTransactions.AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .Select(s => new { s.Id, ContractId = s.ContractId!.Value })
            .ToDictionaryAsync(s => s.Id, s => s.ContractId, ct);
        var saleIds = saleMap.Keys.ToArray();
        var query = _db.LedgerEntries.AsNoTracking()
            .Where(l => (l.ContractId.HasValue && contractIds.Contains(l.ContractId.Value))
                || (l.SourceType == CompanyFlowSourceTypes.Sale && saleIds.Contains(l.SourceId)))
            .WhereEffectiveSarrafSettlementLegs(_db);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }
        var ledgerRows = await query
            .Select(l => new { l.ContractId, l.SourceId, l.EntryDate, l.Side, l.AmountUsd, l.SourceType })
            .ToListAsync(ct);

        var sharesByContract = shareRows.ToLookup(s => s.ContractId);
        foreach (var row in ledgerRows)
        {
            var contractId = row.ContractId
                ?? (row.SourceType == CompanyFlowSourceTypes.Sale
                    && saleMap.TryGetValue(row.SourceId, out var saleContractId)
                        ? saleContractId
                        : (int?)null);
            if (!contractId.HasValue)
            {
                continue;
            }

            foreach (var share in sharesByContract[contractId.Value])
            {
                AddLedgerEvent(
                    target,
                    share.PartnerId,
                    PartyStatementPartyType.Partner,
                    CompanyFlowPartyRole.Partner,
                    row.EntryDate,
                    row.Side,
                    decimal.Round(row.AmountUsd * share.SharePercent / 100m, 2, MidpointRounding.AwayFromZero),
                    row.SourceType);
            }
        }
    }

    private async Task<Dictionary<(PartyStatementPartyType Type, int Id), string>> LoadPartyNamesAsync(
        IReadOnlyCollection<BalanceEvent> events,
        CancellationToken ct)
    {
        var result = new Dictionary<(PartyStatementPartyType, int), string>();

        // فیلتر روی خودِ موجودیت اعمال می‌شود و projection بعد از آن می‌آید. اگر روی
        // یک IQueryable از ValueTuple فیلتر شود، EF کل tuple را مقایسه می‌کند و
        // query ترجمه نمی‌شود؛ این خطا فقط وقتی داده وجود دارد بروز می‌کرد.
        async Task AddAsync<TEntity>(
            PartyStatementPartyType type,
            IQueryable<TEntity> source,
            Expression<Func<TEntity, int>> idSelector,
            Expression<Func<TEntity, PartyNameRow>> projection)
            where TEntity : class
        {
            var ids = events.Where(e => e.PartyType == type).Select(e => e.PartyId).Distinct().ToArray();
            if (ids.Length == 0) return;

            var predicate = Expression.Lambda<Func<TEntity, bool>>(
                Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    [typeof(int)],
                    Expression.Constant(ids),
                    idSelector.Body),
                idSelector.Parameters);

            var rows = await source.AsNoTracking().Where(predicate).Select(projection).ToListAsync(ct);
            foreach (var row in rows) result[(type, row.Id)] = row.Name;
        }

        await AddAsync(PartyStatementPartyType.Customer, _db.Customers,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));
        await AddAsync(PartyStatementPartyType.Supplier, _db.Suppliers,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));
        await AddAsync(PartyStatementPartyType.ServiceProvider, _db.ServiceProviders,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));
        await AddAsync(PartyStatementPartyType.Driver, _db.Drivers,
            x => x.Id, x => new PartyNameRow(x.Id, x.FullName));
        await AddAsync(PartyStatementPartyType.Company, _db.Companies,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));
        await AddAsync(PartyStatementPartyType.Sarraf, _db.Sarrafs,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));
        await AddAsync(PartyStatementPartyType.Employee, _db.Employees,
            x => x.Id, x => new PartyNameRow(x.Id, x.FullName));
        await AddAsync(PartyStatementPartyType.Partner, _db.Partners,
            x => x.Id, x => new PartyNameRow(x.Id, x.Name));

        return result;
    }

    private sealed record PartyNameRow(int Id, string Name);

    private static string? DetailsController(PartyStatementPartyType type) => type switch
    {
        PartyStatementPartyType.Customer => "Customers",
        PartyStatementPartyType.Supplier => "Suppliers",
        PartyStatementPartyType.ServiceProvider => "ServiceProviders",
        PartyStatementPartyType.Driver => "Drivers",
        PartyStatementPartyType.Company => "Companies",
        PartyStatementPartyType.Sarraf => "Sarrafs",
        PartyStatementPartyType.Employee => "Employees",
        PartyStatementPartyType.Partner => "Partners",
        _ => null
    };

    private sealed record BalanceEvent(
        PartyStatementPartyType PartyType,
        int PartyId,
        DateTime Date,
        CompanyFlowDirection Direction,
        decimal AmountUsd);
}
