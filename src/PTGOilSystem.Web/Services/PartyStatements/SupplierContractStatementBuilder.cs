using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// نمای «قراردادها» را می‌سازد: گروه‌بندیِ صرفاً نمایشیِ همان سطرهای مالیِ صورت‌حساب
/// (<see cref="PartyStatementResult.Rows"/>) بر اساس قرارداد. هیچ محاسبهٔ مالی جدیدی انجام
/// نمی‌شود؛ Debit/Credit هر قرارداد فقط جمعِ همان سطرها است، بنابراین جمع کل و مانده نهایی
/// دقیقاً برابر نمای خطی می‌ماند. تابع خالص است تا مستقل از دیتابیس تست شود.
/// </summary>
public static class SupplierContractStatementBuilder
{
    public sealed record ContractFacts(
        string? ProductName,
        decimal? ContractQuantityMt,
        decimal? UnitPriceUsd,
        decimal? ContractValueUsd,
        decimal? LoadedQuantityMt);

    public static SupplierContractStatementViewModel Build(
        PartyStatementResult statement,
        IReadOnlyDictionary<int, ContractFacts> facts)
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
                Debit = g.Sum(r => r.DebitBase ?? 0m),
                Credit = g.Sum(r => r.CreditBase ?? 0m),
                DebitRub = g.Any(r => r.DebitRub.HasValue) ? g.Sum(r => r.DebitRub ?? 0m) : (decimal?)null,
                CreditRub = g.Any(r => r.CreditRub.HasValue) ? g.Sum(r => r.CreditRub ?? 0m) : (decimal?)null
            })
            // قراردادها به‌ترتیب اولین رویداد؛ گروهِ «بدون قرارداد» همیشه آخر.
            .OrderBy(g => g.ContractId.HasValue ? 0 : 1)
            .ThenBy(g => g.FirstDate)
            .ThenBy(g => g.ContractNumber, StringComparer.Ordinal)
            .ToList();

        var rows = new List<SupplierContractStatementRow>(groups.Count);
        var running = opening;
        var runningRub = openingRub ?? 0m;
        var sequence = 1;

        foreach (var g in groups)
        {
            running += g.Credit - g.Debit;
            runningRub += (g.CreditRub ?? 0m) - (g.DebitRub ?? 0m);

            ContractFacts? f = null;
            if (g.ContractId.HasValue)
            {
                facts.TryGetValue(g.ContractId.Value, out f);
            }

            var title = g.ContractId.HasValue
                ? string.IsNullOrWhiteSpace(f?.ProductName)
                    ? $"قرارداد #{g.ContractNumber}"
                    : $"قرارداد #{g.ContractNumber} • {f!.ProductName}"
                : "بدون قرارداد";

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
                Debit = g.Debit,
                Credit = g.Credit,
                Balance = running,
                DebitRub = g.DebitRub,
                CreditRub = g.CreditRub,
                BalanceRub = isRub ? runningRub : null
            });
        }

        return new SupplierContractStatementViewModel
        {
            Rows = rows,
            HasOpeningRow = hasOpeningRow,
            OpeningBalance = opening,
            OpeningBalanceRub = openingRub,
            IsRub = isRub,
            TotalDebit = statement.Summary.TotalDebit,
            TotalCredit = statement.Summary.TotalCredit,
            ClosingBalance = statement.Summary.ClosingBalance,
            TotalDebitRub = statement.Summary.TotalDebitRub,
            TotalCreditRub = statement.Summary.TotalCreditRub,
            ClosingBalanceRub = statement.Summary.ClosingBalanceRub
        };
    }
}
