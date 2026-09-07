using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Payments;

public sealed class CashAccountIndexFilterViewModel
{
    [Display(Name = "جستجو")]
    [StringLength(200)]
    public string? Query { get; set; }

    [Display(Name = "نوع حساب")]
    public CashAccountType? AccountType { get; set; }

    [Display(Name = "ارز")]
    [StringLength(10)]
    public string? Currency { get; set; }

    [Display(Name = "وضعیت")]
    public bool? IsActive { get; set; }
}

public sealed class CashAccountIndexViewModel
{
    public CashAccountIndexFilterViewModel Filter { get; init; } = new();
    public IReadOnlyList<CashAccount> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
    public FinanceMetricCardsViewModel FinanceMetrics { get; init; } = new();
}

public static class CashAccountTypeLabels
{
    public static string ToPersian(CashAccountType accountType) => accountType switch
    {
        CashAccountType.Cash => "نقد",
        CashAccountType.Bank => "بانک",
        CashAccountType.Mixed => "مختلط (همه ارزها)",
        _ => accountType.ToString()
    };
}

/// <summary>
/// یک سطر از گردشِ صندوق/بانک.
///
/// دو نوع سند روی یک صندوق حرکتِ واقعیِ پول می‌سازند: رزنامچه
/// (<see cref="PaymentTransaction"/>) و مصرفی که «نقد پرداخت شد» ثبت شده است
/// (<see cref="ExpenseTransaction.CashAccountId"/> با
/// <see cref="ExpenseSettlementMode.PaidImmediately"/> — مثل گمرکِ نقدی).
/// صفحهٔ جزئیات پیش از این فقط رزنامچه را می‌خواند، پس پرداختِ نقدیِ مصارف نه در
/// فهرست دیده می‌شد و نه در مانده. این سطر هر دو را در یک شکلِ واحد نشان می‌دهد؛
/// هیچ سندِ تازه‌ای ساخته نمی‌شود و دفتر کل دست‌نخورده می‌ماند.
/// </summary>
public sealed class CashAccountStatementRowViewModel
{
    public int Id { get; init; }

    /// <summary>«Payment» یا «Expense» — تعیین می‌کند لینکِ مرجع به کدام صفحه برود.</summary>
    public string Source { get; init; } = "Payment";

    public DateTime EntryDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string KindName { get; init; } = string.Empty;
    public string CounterpartyName { get; init; } = string.Empty;
    public string? ContractNumber { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public string? Reference { get; init; }
    public int? LedgerEntryId { get; init; }
}
