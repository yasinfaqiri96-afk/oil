namespace PTGOilSystem.Web.Models.Finance;

/// <summary>
/// صورت‌حساب پرداخت‌های بین مشتریان برای یک مشتری. فقط از جدول CustomerToCustomerPayments خوانده
/// می‌شود و هیچ ارتباطی با دفتر کل یا ماندهٔ مشتری نزد شرکت ندارد. مانده = دریافتی − پرداختی، جدا برای هر ارز.
/// </summary>
public sealed class CustomerToCustomerStatementViewModel
{
    public int? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public int? CounterpartyId { get; set; }
    public string? Currency { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public List<CustomerToCustomerStatementRow> Rows { get; } = new();
    public List<CustomerToCustomerStatementTotal> Totals { get; } = new();
}

public sealed record CustomerToCustomerStatementRow(
    int Id,
    DateTime Date,
    string CounterpartyName,
    decimal Received,
    decimal Paid,
    string Currency,
    decimal RunningBalance,
    string? Reference,
    string? Description);

public sealed record CustomerToCustomerStatementTotal(
    string Currency,
    decimal Opening,
    decimal Received,
    decimal Paid,
    decimal Closing);
