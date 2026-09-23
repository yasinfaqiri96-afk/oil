using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Contracts;

public sealed class ContractIndexViewModel
{
    public string? Query { get; init; }
    public IReadOnlyList<ContractType> Types { get; init; } = [];
    public IReadOnlyList<ContractStatus> Statuses { get; init; } = [];
    public IReadOnlyList<Contract> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public int PurchaseCount { get; init; }
    public int SaleCount { get; init; }
}
