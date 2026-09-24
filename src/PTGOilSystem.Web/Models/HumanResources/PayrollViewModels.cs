using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.HumanResources;

public static class PayrollLabels
{
    public static string Status(PayrollRunStatus status) => status switch
    {
        PayrollRunStatus.Draft => "پیش‌نویس",
        PayrollRunStatus.Finalized => "نهایی‌شده",
        _ => status.ToString()
    };

    public static string PaymentStatus(decimal net, decimal paid) => net <= 0m
        ? "بدون پرداخت"
        : paid <= 0m ? "پرداخت‌نشده" : paid >= net ? "پرداخت‌شده" : "پرداخت ناقص";

    public static string PaymentCss(decimal net, decimal paid) => net <= 0m
        ? "is-inactive"
        : paid <= 0m ? "is-danger" : paid >= net ? "is-active" : "is-warning";
}

public sealed class PayrollLineViewModel
{
    public PayrollRunLine Line { get; init; } = new();
    public string EmployeeName { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string? DepartmentName { get; init; }
    public decimal Paid { get; init; }
    public decimal Remaining => Math.Max(0m, Line.NetSalary - Paid);
}

public sealed class PayrollCurrencyTotal
{
    public string Currency { get; init; } = "USD";
    public decimal Base { get; init; }
    public decimal Earnings { get; init; }
    public decimal Deductions { get; init; }
    public decimal Net { get; init; }
    public decimal Paid { get; init; }
}

public sealed class PayrollIndexViewModel
{
    public int Year { get; init; }
    public int Month { get; init; }
    public PayrollRun? Run { get; init; }
    public IReadOnlyList<PayrollLineViewModel> Lines { get; init; } = [];
    public IReadOnlyList<PayrollCurrencyTotal> Totals { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public bool CanRun { get; init; }
    public bool CanPay { get; init; }
    public bool IsDraft => Run?.Status == PayrollRunStatus.Draft;
    public bool IsFinalized => Run?.Status == PayrollRunStatus.Finalized;
}

public sealed class PayrollLineInput
{
    public int LineId { get; set; }
    public decimal OvertimeAmount { get; set; }
    public decimal BonusAmount { get; set; }
    public decimal AllowanceAmount { get; set; }
    public decimal OtherEarning { get; set; }
    public decimal OtherDeduction { get; set; }
    public string? Notes { get; set; }
}

public sealed class PayrollPayRowInput
{
    public int LineId { get; set; }
    public bool Selected { get; set; }
    public decimal Amount { get; set; }
}
