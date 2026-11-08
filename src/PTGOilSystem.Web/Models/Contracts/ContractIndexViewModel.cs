using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Contracts;

public sealed class ContractIndexViewModel
{
    public string? Query { get; init; }
    public IReadOnlyList<ContractType> Types { get; init; } = [];
    public IReadOnlyList<ContractStatus> Statuses { get; init; } = [];
    /// <summary>همهٔ قراردادهای این صفحه به ترتیب نمایش: قرارداد تکی و ردیف‌های قرارداد مادر.</summary>
    public IReadOnlyList<Contract> Items { get; init; } = [];
    /// <summary>ردیف‌های اصلی فهرست: هر قرارداد تکی یک ردیف، هر قرارداد چندتأمین‌کننده یک ردیف.</summary>
    public IReadOnlyList<ContractListRow> Rows { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public int PurchaseCount { get; init; }
    public int SaleCount { get; init; }
}

/// <summary>یکی از دو: <see cref="Contract"/> تکی یا <see cref="Group"/> قرارداد مادر.</summary>
public sealed class ContractListRow
{
    public Contract? Contract { get; init; }
    public PurchaseGroupListRow? Group { get; init; }
}

public sealed class PurchaseGroupListRow
{
    public int Id { get; init; }
    public string GroupNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string ProductNames { get; init; } = "";
    public string UnitName { get; init; } = "";
    public ContractStatus Status { get; init; }
    public IReadOnlyList<Contract> Lines { get; init; } = [];
    public PurchaseGroupTotals Totals { get; init; } = PurchaseGroupTotals.Calculate([]);

    public string DisplayLabel => Contract.BuildDisplayLabel(Name, GroupNumber);
}

public sealed record ContractListKey(int Id, int? PurchaseContractGroupId, ContractType ContractType, ContractStatus Status);

/// <summary>یک واحد فهرست: قرارداد تکی (ContractId) یا قرارداد مادر (GroupId).</summary>
public sealed record ContractListUnit(int FirstContractId, int? ContractId, int? GroupId, ContractType ContractType, ContractStatus Status);

public static class ContractListGrouping
{
    /// <summary>
    /// ردیف‌های یک قرارداد چندتأمین‌کننده یک واحد می‌شوند تا شمارش، آمار و صفحه‌بندی تعداد
    /// قراردادهای مادر را بشمارد، نه تعداد ردیف‌ها. ترتیب همان ترتیب Id قبلی است.
    /// </summary>
    public static IReadOnlyList<ContractListUnit> Build(IEnumerable<ContractListKey> keys)
        => keys
            .GroupBy(key => key.PurchaseContractGroupId is { } groupId ? (IsGroup: true, Id: groupId) : (IsGroup: false, Id: key.Id))
            .Select(group =>
            {
                var rows = group.ToList();
                return new ContractListUnit(
                    rows.Min(row => row.Id),
                    group.Key.IsGroup ? null : group.Key.Id,
                    group.Key.IsGroup ? group.Key.Id : null,
                    rows[0].ContractType,
                    group.Key.IsGroup ? PurchaseGroupStatus.Derive(rows.Select(row => row.Status)) : rows[0].Status);
            })
            .OrderBy(unit => unit.FirstContractId)
            .ToList();
}

public static class PurchaseGroupStatus
{
    /// <summary>
    /// وضعیت نمایشی قرارداد مادر از ردیف‌ها: یک ردیف فعال یعنی قرارداد فعال است؛ لغو فقط وقتی
    /// همهٔ ردیف‌ها لغو شده باشند. این وضعیت ذخیره نمی‌شود.
    /// </summary>
    public static ContractStatus Derive(IEnumerable<ContractStatus> lineStatuses)
    {
        var statuses = lineStatuses.ToList();
        if (statuses.Contains(ContractStatus.Active)) return ContractStatus.Active;
        if (statuses.Contains(ContractStatus.Draft)) return ContractStatus.Draft;
        if (statuses.Contains(ContractStatus.Closed)) return ContractStatus.Closed;
        return statuses.Count == 0 ? ContractStatus.Draft : ContractStatus.Cancelled;
    }
}
