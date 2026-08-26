using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// نمای «قراردادها» را می‌سازد: گروه‌بندیِ صرفاً نمایشیِ همان سطرهای مالیِ صورت‌حساب
/// (<see cref="PartyStatementResult.Rows"/>) بر اساس قرارداد. هیچ محاسبهٔ مالی جدیدی انجام
/// نمی‌شود؛ رسید/برد هر قرارداد فقط جمعِ همان سطرها است، بنابراین جمع کل و بیلانس نهایی
/// دقیقاً برابر نمای خطی می‌ماند. تابع خالص است تا مستقل از دیتابیس تست شود.
/// </summary>
public static class SupplierContractStatementBuilder
{
    public sealed record ContractFacts(
        string? ProductName,
        decimal? ContractQuantityMt,
        decimal? UnitPriceUsd,
        decimal? ContractValueUsd,
        decimal? LoadedQuantityMt,
        decimal? SharePercent = null);

    public static SupplierContractStatementViewModel Build(
        PartyStatementResult statement,
        IReadOnlyDictionary<int, ContractFacts> facts,
        int page = 1,
        int pageSize = 20)
    {
        var isRub = statement.Summary.IsRubPresentation;
        var opening = statement.Summary.OpeningBalance;
        var openingRub = statement.Summary.OpeningBalanceRub;
        var hasOpeningRow = statement.Rows.Any(r => r.IsOpeningBalance);

        var periodRows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        // گروه‌بندی بر اساس قرارداد؛ سطرهای بدون قرارداد در گروهِ جداگانهٔ «بدون قرارداد».
        var groups = periodRows
            .GroupBy(r => r.ContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                ContractNumber = g.Select(r => r.ContractNumber).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                FirstDate = g.Min(r => r.Date),
                LastDate = g.Max(r => r.Date),
                Receipt = g.Sum(r => r.ReceiptBase ?? 0m),
                Outflow = g.Sum(r => r.OutflowBase ?? 0m),
                ConfirmedValue = g.Where(IsConfirmedOperation)
                    .Sum(r => (r.ReceiptBase ?? 0m) + (r.OutflowBase ?? 0m)),
                // «پرداخت / دریافت» خالص است، نه جمعِ بی‌جهت. جمعِ بی‌جهت یک انتقال داخلی
                // را دو بار می‌شمرد: یک بار به‌عنوان خروج از پیش‌پرداخت آزاد و یک بار
                // به‌عنوان ورود به قرارداد — و پای برگشتی را هم به‌جای کم‌کردن، اضافه می‌کرد.
                SettlementTotal = g.Where(r => !IsConfirmedOperation(r))
                    .Sum(r => (r.OutflowBase ?? 0m) - (r.ReceiptBase ?? 0m)),
                ConfirmedValueRub = g.Where(IsConfirmedOperation).Any(r => r.ReceiptRub.HasValue || r.OutflowRub.HasValue)
                    ? g.Where(IsConfirmedOperation).Sum(r => (r.ReceiptRub ?? 0m) + (r.OutflowRub ?? 0m))
                    : (decimal?)null,
                SettlementTotalRub = g.Where(r => !IsConfirmedOperation(r)).Any(r => r.ReceiptRub.HasValue || r.OutflowRub.HasValue)
                    ? g.Where(r => !IsConfirmedOperation(r)).Sum(r => (r.OutflowRub ?? 0m) - (r.ReceiptRub ?? 0m))
                    : (decimal?)null,
                LoadingCount = g.Where(IsConfirmedOperation)
                    .Select(r => (r.SourceType, r.SourceId))
                    .Distinct()
                    .Count(),
                ReceiptRub = g.Any(r => r.ReceiptRub.HasValue) ? g.Sum(r => r.ReceiptRub ?? 0m) : (decimal?)null,
                OutflowRub = g.Any(r => r.OutflowRub.HasValue) ? g.Sum(r => r.OutflowRub ?? 0m) : (decimal?)null
            })
            // قراردادها به‌ترتیب اولین رویداد؛ گروهِ «بدون قرارداد» همیشه آخر.
            .OrderBy(g => g.ContractId.HasValue ? 0 : 1)
            .ThenBy(g => g.FirstDate)
            .ThenBy(g => g.ContractNumber, StringComparer.Ordinal)
            .ToList();

        var rows = new List<SupplierContractStatementRow>(groups.Count);
        var sequence = 1;

        foreach (var g in groups)
        {
            ContractFacts? f = null;
            if (g.ContractId.HasValue)
            {
                facts.TryGetValue(g.ContractId.Value, out f);
            }

            var title = g.ContractId.HasValue
                ? string.IsNullOrWhiteSpace(f?.ProductName)
                    ? $"قرارداد #{g.ContractNumber}"
                    : $"قرارداد #{g.ContractNumber} • {f!.ProductName}"
                : "بدون قرارداد / عمومی";

            rows.Add(new SupplierContractStatementRow
            {
                Sequence = sequence++,
                ContractId = g.ContractId,
                ContractNumber = g.ContractNumber,
                FirstDate = g.FirstDate,
                LastDate = g.LastDate,
                Title = title,
                ProductName = f?.ProductName,
                ContractQuantityMt = f?.ContractQuantityMt,
                UnitPriceUsd = f?.UnitPriceUsd,
                ContractValueUsd = f?.ContractValueUsd,
                LoadedQuantityMt = f?.LoadedQuantityMt,
                SharePercent = f?.SharePercent,
                ConfirmedValue = g.ConfirmedValue,
                SettlementTotal = g.SettlementTotal,
                ConfirmedValueRub = g.ConfirmedValueRub,
                SettlementTotalRub = g.SettlementTotalRub,
                LoadingCount = g.LoadingCount,
                Receipt = g.Receipt,
                Outflow = g.Outflow,
                Balance = g.Outflow - g.Receipt,
                ReceiptRub = g.ReceiptRub,
                OutflowRub = g.OutflowRub,
                BalanceRub = isRub ? (g.OutflowRub ?? 0m) - (g.ReceiptRub ?? 0m) : null
            });
        }

        return new SupplierContractStatementViewModel
        {
            Rows = rows,
            HasOpeningRow = hasOpeningRow,
            OpeningBalance = opening,
            OpeningBalanceRub = openingRub,
            IsRub = isRub,
            TotalReceipt = statement.Summary.TotalReceipt,
            TotalOutflow = statement.Summary.TotalOutflow,
            ClosingBalance = statement.Summary.ClosingBalance,
            TotalReceiptRub = statement.Summary.TotalReceiptRub,
            TotalOutflowRub = statement.Summary.TotalOutflowRub,
            ClosingBalanceRub = statement.Summary.ClosingBalanceRub,
            TotalConfirmedValue = rows.Sum(r => r.ConfirmedValue),
            TotalSettlement = rows.Sum(r => r.SettlementTotal),
            TotalConfirmedValueRub = rows.Any(r => r.ConfirmedValueRub.HasValue)
                ? rows.Sum(r => r.ConfirmedValueRub ?? 0m)
                : null,
            TotalSettlementRub = rows.Any(r => r.SettlementTotalRub.HasValue)
                ? rows.Sum(r => r.SettlementTotalRub ?? 0m)
                : null,
            TotalLoadingCount = rows.Sum(r => r.LoadingCount),
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 10, 100)
        };
    }

    /// <summary>
    /// گردش حساب فشرده: فقط رویدادهای عملیاتی تکراری (بارگیری/فروش) در سطح قرارداد
    /// جمع می‌شوند. پرداخت، دریافت، حوالهٔ صراف، معاش و سایر اسناد مستقل عیناً باقی
    /// می‌مانند. جمع و ماندهٔ نهایی از همان مبالغ پایه دوباره مرتب می‌شود.
    /// </summary>
    public static IReadOnlyList<PartyStatementRow> BuildCompactLedgerRows(PartyStatementResult statement)
    {
        var openingRow = statement.Rows.FirstOrDefault(r => r.IsOpeningBalance);
        var periodRows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();
        var independent = periodRows.Where(r => !IsConfirmedOperation(r) || !r.ContractId.HasValue).ToList();
        var aggregates = periodRows
            .Where(r => IsConfirmedOperation(r) && r.ContractId.HasValue)
            .GroupBy(r => r.ContractId!.Value)
            .Select(BuildOperationAggregate)
            .ToList();

        var ordered = independent.Concat(aggregates)
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

    private static PartyStatementRow BuildOperationAggregate(IGrouping<int, PartyStatementRow> group)
    {
        var rows = group.ToList();
        var first = rows.OrderBy(r => r.Date).ThenBy(r => r.PostingSequence).First();
        var last = rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.PostingSequence).First();
        var loadingLabel = rows.All(r => r.SourceType == "Sale") ? "فروش/تحویل" : "بارگیری";
        var receiptRubKnown = rows.Any(r => r.ReceiptRub.HasValue);
        var outflowRubKnown = rows.Any(r => r.OutflowRub.HasValue);

        return new PartyStatementRow
        {
            Date = last.Date,
            CreatedAtUtc = last.CreatedAtUtc,
            Reference = last.ContractNumber ?? first.Reference,
            Description = $"مجموع {rows.Count:N0} سند {loadingLabel} قرارداد {last.ContractNumber ?? group.Key.ToString()}",
            ReceiptBase = rows.Sum(r => r.ReceiptBase ?? 0m),
            OutflowBase = rows.Sum(r => r.OutflowBase ?? 0m),
            ReceiptRub = receiptRubKnown ? rows.Sum(r => r.ReceiptRub ?? 0m) : null,
            OutflowRub = outflowRubKnown ? rows.Sum(r => r.OutflowRub ?? 0m) : null,
            OriginalCurrency = "USD",
            Quantity = rows.Any(r => r.Quantity.HasValue) ? rows.Sum(r => r.Quantity ?? 0m) : null,
            QuantityUnit = rows.Select(r => r.QuantityUnit).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            SourceType = "ContractOperations",
            SourceId = group.Key,
            PostingSequence = rows.Min(r => r.PostingSequence),
            ContractId = group.Key,
            ContractNumber = last.ContractNumber ?? first.ContractNumber
        };
    }

    private static bool IsConfirmedOperation(PartyStatementRow row)
        => row.SourceType is "Loading" or "Sale";
}
