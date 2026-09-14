using PTGOilSystem.Web.Services.ContractClosure;

namespace PTGOilSystem.Web.Models.Contracts;

/// <summary>صفحهٔ بستن / بازگشایی قرارداد: فهرست موارد باز و تاریخچهٔ همین دو عملیات.</summary>
public sealed class ContractClosureViewModel
{
    public int ContractId { get; init; }
    public string ContractLabel { get; init; } = string.Empty;
    public string StatusName { get; init; } = string.Empty;
    public bool IsReopen { get; init; }
    public bool CanSubmit { get; init; }
    public IReadOnlyList<ContractClosureBlocker> Blockers { get; init; } = [];
    public IReadOnlyList<ContractClosureHistoryRow> History { get; init; } = [];
    public string? ReturnUrl { get; init; }
}

public sealed record ContractClosureHistoryRow(DateTime ActionAtUtc, string Action, string? ActorUsername, string? Description);
