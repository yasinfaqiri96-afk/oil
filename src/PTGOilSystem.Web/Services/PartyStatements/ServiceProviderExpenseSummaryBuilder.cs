using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// نمای «خلاصه» صورت‌حساب شرکت خدماتی: سطرهای دوره بر اساس نوع مصرف (و برای اسناد
/// غیرمصرف، بر اساس نوع سند) در یک سطر جمع می‌شوند تا به‌جای دانه‌دانهٔ مصارف، یک سطر
/// «مجموع N سند کرایه» دیده شود.
///
/// گروه‌بندی کاملاً نمایشی است: هیچ مبلغی محاسبه نمی‌شود، فقط همان مبالغ سطرهای موجود
/// جمع می‌شوند؛ جمع دوره، بیلانس اول و بیلانس نهایی دست‌نخورده می‌مانند.
/// </summary>
public static class ServiceProviderExpenseSummaryBuilder
{
    public static IReadOnlyList<PartyStatementRow> Build(PartyStatementResult statement)
    {
        var openingRow = statement.Rows.FirstOrDefault(r => r.IsOpeningBalance);
        var periodRows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        // اسناد لغوشده و برگشتی در گروه نمی‌روند تا چرخهٔ عمرشان در نمایش گم نشود.
        var independent = periodRows.Where(r => r.IsCancelled || r.IsReversalRow).ToList();
        var groups = periodRows
            .Where(r => !r.IsCancelled && !r.IsReversalRow)
            .GroupBy(r => new
            {
                Label = GroupLabel(r),
                r.FlowDirection
            })
            .ToList();

        var aggregated = new List<PartyStatementRow>(groups.Count);
        foreach (var group in groups)
        {
            var rows = group.ToList();
            // گروه تک‌سندی چیزی را ساده نمی‌کند و مرجع/پیوند سند را از دست می‌دهد.
            if (rows.Count == 1)
            {
                aggregated.Add(rows[0]);
                continue;
            }

            aggregated.Add(BuildAggregate(rows, group.Key.Label));
        }

        var ordered = independent.Concat(aggregated)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.PostingSequence)
            .ThenBy(r => r.SourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceId)
            .ToList();

        var result = new List<PartyStatementRow>(ordered.Count + (openingRow is null ? 0 : 1));
        var running = openingRow?.RunningBalance ?? statement.Summary.OpeningBalance;
        var runningRub = openingRow?.RunningBalanceRub ?? statement.Summary.OpeningBalanceRub;
        if (openingRow is not null)
        {
            result.Add(openingRow);
        }

        var sequence = 1;
        foreach (var row in ordered)
        {
            running += row.SignedAmount;
            row.Sequence = sequence++;
            row.RunningBalance = running;
            if (runningRub.HasValue && row.SignedAmountRub.HasValue)
            {
                runningRub += row.SignedAmountRub.Value;
                row.RunningBalanceRub = runningRub;
            }
            result.Add(row);
        }

        return result;
    }

    /// <summary>در دوره دست‌کم یک گروه چندسندی وجود دارد، یعنی خلاصه چیزی را جمع می‌کند.</summary>
    public static bool HasGroupableRows(PartyStatementResult statement)
        => statement.Rows
            .Where(r => !r.IsOpeningBalance && !r.IsCancelled && !r.IsReversalRow)
            .GroupBy(r => new { Label = GroupLabel(r), r.FlowDirection })
            .Any(g => g.Count() > 1);

    private static PartyStatementRow BuildAggregate(List<PartyStatementRow> rows, string label)
    {
        var last = rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.PostingSequence).First();
        var receiptRubKnown = rows.Any(r => r.ReceiptRub.HasValue);
        var outflowRubKnown = rows.Any(r => r.OutflowRub.HasValue);
        var currencies = rows.Select(r => r.OriginalCurrency).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var singleCurrency = currencies.Count == 1 ? currencies[0] : "USD";

        return new PartyStatementRow
        {
            Date = last.Date,
            CreatedAtUtc = last.CreatedAtUtc,
            Description = $"مجموع {rows.Count:N0} سند {label}",
            CategoryLabel = label,
            ReceiptBase = rows.Any(r => r.ReceiptBase.HasValue) ? rows.Sum(r => r.ReceiptBase ?? 0m) : null,
            OutflowBase = rows.Any(r => r.OutflowBase.HasValue) ? rows.Sum(r => r.OutflowBase ?? 0m) : null,
            ReceiptRub = receiptRubKnown ? rows.Sum(r => r.ReceiptRub ?? 0m) : null,
            OutflowRub = outflowRubKnown ? rows.Sum(r => r.OutflowRub ?? 0m) : null,
            // مبلغ اصلی فقط وقتی معنی دارد که همهٔ اسناد گروه یک ارز داشته باشند.
            OriginalAmount = currencies.Count == 1 && rows.Any(r => r.OriginalAmount.HasValue)
                ? rows.Sum(r => r.OriginalAmount ?? 0m)
                : null,
            OriginalCurrency = singleCurrency,
            Quantity = rows.Any(r => r.Quantity.HasValue) ? rows.Sum(r => r.Quantity ?? 0m) : null,
            QuantityUnit = rows.Select(r => r.QuantityUnit).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            SourceType = last.SourceType,
            SourceId = 0,
            PostingSequence = rows.Min(r => r.PostingSequence),
            FlowDirection = last.FlowDirection
        };
    }

    private static string GroupLabel(PartyStatementRow row)
        => !string.IsNullOrWhiteSpace(row.CategoryLabel)
            ? row.CategoryLabel!
            : SourceTypeLabel(row.SourceType);

    private static string SourceTypeLabel(string sourceType) => sourceType switch
    {
        "Expense" => "مصرف",
        "Loading" => "بارگیری",
        "Sale" => "فروش / تحویل",
        "SupplierPayment" => "پرداخت",
        "CustomerReceipt" => "دریافت",
        "SarrafSettlement" => "سند صراف",
        "PaymentTransaction" => "تراکنش پرداخت",
        _ => string.IsNullOrWhiteSpace(sourceType) ? "سایر" : sourceType
    };
}
