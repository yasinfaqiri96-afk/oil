using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.HumanResources;

public sealed record HrCurrencyTotal(string Currency, decimal Amount);

public sealed record HrDashboardAlertRow(string EmployeeName, string Reference, DateTime? Date, int Id);

public sealed class HrDashboardViewModel
{
    public DateTime Today { get; init; }
    public bool CanViewSalary { get; init; }
    public int TotalEmployees { get; init; }
    public int ActiveEmployees { get; init; }
    public int PresentToday { get; init; }
    public int AbsentToday { get; init; }
    public int OnLeaveToday { get; init; }
    public int NotRecordedToday { get; init; }
    public int PendingLeaveRequests { get; init; }
    public int ExpiringDocuments { get; init; }
    public IReadOnlyList<HrDashboardAlertRow> ExpiringContracts { get; init; } = [];
    public PayrollRunStatus? CurrentPayrollStatus { get; set; }
    public IReadOnlyList<HrCurrencyTotal> CurrentPayrollNet { get; set; } = [];
    public IReadOnlyList<HrCurrencyTotal> UnpaidSalaries { get; set; } = [];
    public IReadOnlyList<HrCurrencyTotal> OutstandingLoansAndAdvances { get; set; } = [];

    public static string Amounts(IEnumerable<HrCurrencyTotal> totals)
    {
        var list = totals.ToList();
        return list.Count == 0 ? "0" : string.Join(" + ", list.Select(t => $"{t.Amount:N0} {t.Currency}"));
    }
}
