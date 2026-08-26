using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// خروجی Excel/PDF صورت‌حساب شراکت. دقیقاً همان ارقامِ صفحه را می‌نویسد — از همان
/// <see cref="IPartnershipStatementService"/> — و هیچ محاسبهٔ جداگانه‌ای ندارد.
/// </summary>
public partial class PartnershipStatementController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(
        int? partnerAId = null,
        int? partnerBId = null,
        int[]? contractIds = null,
        string? format = null,
        CancellationToken ct = default)
    {
        var pairs = await _statements.ListPairsAsync(ct);
        if (pairs.Count == 0)
        {
            return NotFound();
        }

        var pair = pairs.FirstOrDefault(p =>
            (p.PartnerAId == partnerAId && p.PartnerBId == partnerBId)
            || (p.PartnerAId == partnerBId && p.PartnerBId == partnerAId))
            ?? pairs[0];

        var statement = await _statements.BuildAsync(pair.PartnerAId, pair.PartnerBId, contractIds, ct);
        if (statement is null)
        {
            return NotFound();
        }

        var sheets = new List<TabularExportDocument> { BuildSummarySheet(statement) };
        sheets.AddRange(statement.Contracts.Select(c => BuildContractSheet(statement, c)));
        sheets.Add(BuildSettlementSheet(statement));

        return TabularExportSupport.File(this, format, sheets);
    }

    private static string Money(decimal value) => value.ToString("N2");

    private static IReadOnlyList<TabularExportFilter> BuildFilters(PartnershipStatement statement)
        => TabularExportSupport.FilterSummary(
            ("شرکا", $"{statement.PartnerAName} ↔ {statement.PartnerBName}"),
            ("قراردادها", string.Join("، ", statement.ContractOptions
                .Where(o => o.IsSelected)
                .Select(o => o.ContractLabel))));

    private static TabularExportDocument BuildSummarySheet(PartnershipStatement statement)
    {
        var a = statement.Totals[0];
        var b = statement.Totals[1];

        TabularExportRow Row(string label, decimal left, decimal right)
            => new([
                TabularExportCell.Text(label),
                TabularExportCell.Number(left),
                TabularExportCell.Number(right)
            ]);

        var rows = new List<TabularExportRow>
        {
            Row("پرداخت / سرمایهٔ گذاشته‌شده", a.FundingUsd, b.FundingUsd),
            Row("عواید فروش نزد شریک", a.ProceedsHeldUsd, b.ProceedsHeldUsd),
            Row("سهم مفاد", a.ProfitShareUsd, b.ProfitShareUsd),
            Row("تسویهٔ پرداخت‌شده به شریک دیگر", a.SettlementsPaidUsd, b.SettlementsPaidUsd),
            Row("تسویهٔ دریافت‌شده از شریک دیگر", a.SettlementsReceivedUsd, b.SettlementsReceivedUsd),
            Row("مانده — مثبت یعنی طلبکار، منفی یعنی بدهکار", a.NetPositionUsd, b.NetPositionUsd)
        };

        var bookProfit = statement.Contracts.Sum(c => c.BookProfitUsd);
        var paymentToBook = statement.Contracts.Sum(c => c.PaymentToBookDifferenceUsd);
        rows.Add(new TabularExportRow([
            TabularExportCell.Text("مفاد قرارداد — فروش منهای خرید و مصارف"),
            TabularExportCell.Number(bookProfit),
            TabularExportCell.Text(string.Empty)
        ]));
        rows.Add(new TabularExportRow([
            TabularExportCell.Text("تفاوت تطبیق پرداخت با دفتر"),
            TabularExportCell.Number(paymentToBook),
            TabularExportCell.Text(string.Empty)
        ]));
        rows.Add(new TabularExportRow([
            TabularExportCell.Text("باقیماندهٔ تطبیق‌نشده — جمع مانده دو شریک"),
            TabularExportCell.Number(statement.UnreconciledResidualUsd),
            TabularExportCell.Text(string.Empty)
        ]));

        if (statement.DebtorPartnerId.HasValue)
        {
            rows.Add(new TabularExportRow([
                TabularExportCell.Text(
                    $"مانده نهایی: {statement.DebtorPartnerName} ← {statement.CreditorPartnerName}"),
                TabularExportCell.Number(statement.AmountDueUsd),
                TabularExportCell.Text(string.Empty)
            ]));
        }

        return new TabularExportDocument
        {
            FileNameStem = "partnership-statement",
            TitleFa = "صورت‌حساب شراکت — خلاصه",
            TitleEn = "Partnership statement — summary",
            Columns =
            [
                new TabularExportColumn("شرح", "Description", TabularExportValueType.Text, 46, Wrap: true),
                new TabularExportColumn(a.PartnerName, a.PartnerName, TabularExportValueType.Number, 20),
                new TabularExportColumn(b.PartnerName, b.PartnerName, TabularExportValueType.Number, 20)
            ],
            Rows = rows,
            Filters = BuildFilters(statement),
            KnownRowCount = rows.Count
        };
    }

    private static TabularExportDocument BuildContractSheet(
        PartnershipStatement statement,
        PartnershipContractStatement contract)
    {
        TabularExportRow Row(string label, string source, decimal amount)
            => new([
                TabularExportCell.Text(label),
                TabularExportCell.Text(source),
                TabularExportCell.Number(amount)
            ]);

        var rows = new List<TabularExportRow>();
        foreach (var partner in contract.Partners)
        {
            rows.Add(Row($"پرداخت {partner.PartnerName}", PartnershipStatementSources.Payment, partner.FundingUsd));
        }

        rows.Add(Row("جمع پرداخت شرکا", PartnershipStatementSources.Payment, contract.TotalPartnerFundingUsd));
        rows.Add(Row(
            string.IsNullOrWhiteSpace(contract.ProceedsHolderPartnerName)
                ? "فروش — نگهدارندهٔ عاید ثبت نشده"
                : $"فروش — عاید نزد {contract.ProceedsHolderPartnerName}",
            PartnershipStatementSources.Sale,
            contract.SalesUsd));
        rows.Add(Row("خرید ثبت‌شده — بارگیری قرارداد", PartnershipStatementSources.Loading, contract.PurchaseCostUsd));
        rows.Add(Row("مصارف ثبت‌شدهٔ قرارداد", PartnershipStatementSources.Expense, contract.OperationalExpenseUsd));
        rows.Add(Row("مفاد قرارداد — فروش منهای خرید و مصارف", PartnershipStatementSources.Book, contract.BookProfitUsd));

        foreach (var partner in contract.Partners)
        {
            rows.Add(Row(
                $"سهم مفاد {partner.PartnerName} — {partner.SharePercent:0.##}٪",
                PartnershipStatementSources.Book,
                partner.ProfitShareUsd));
        }

        rows.Add(Row(
            "تفاوت تطبیق پرداخت با دفتر",
            PartnershipStatementSources.Reconciliation,
            contract.PaymentToBookDifferenceUsd));

        foreach (var partner in contract.Partners)
        {
            rows.Add(Row($"مانده {partner.PartnerName} در این قرارداد", "—", partner.NetPositionUsd));
        }

        if (contract.UnreconciledResidualUsd != 0m)
        {
            rows.Add(Row(
                "باقیماندهٔ تطبیق‌نشده — جمع مانده دو شریک",
                PartnershipStatementSources.Reconciliation,
                contract.UnreconciledResidualUsd));
        }

        // ردیف‌های تشکیل‌دهنده، همان drill-down صفحه.
        foreach (var line in contract.Lines)
        {
            var owner = line.PartnerId.HasValue
                ? statement.Totals.FirstOrDefault(t => t.PartnerId == line.PartnerId.Value)?.PartnerName
                : null;
            rows.Add(new TabularExportRow([
                TabularExportCell.Text(
                    $"{line.Date?.ToString("yyyy-MM-dd") ?? "—"} · {line.Title} · {owner ?? "—"}"),
                TabularExportCell.Text(line.Source),
                TabularExportCell.Number(line.AmountUsd)
            ]));
        }

        return new TabularExportDocument
        {
            FileNameStem = $"partnership-{contract.ContractNumber}",
            TitleFa = $"صورت‌حساب شراکت — {contract.ContractLabel}",
            TitleEn = $"Partnership statement — {contract.ContractNumber}",
            Columns =
            [
                new TabularExportColumn("شرح", "Description", TabularExportValueType.Text, 52, Wrap: true),
                new TabularExportColumn("منبع", "Source", TabularExportValueType.Text, 16),
                new TabularExportColumn("مبلغ (USD)", "Amount (USD)", TabularExportValueType.Number, 20)
            ],
            Rows = rows,
            Filters = BuildFilters(statement),
            KnownRowCount = rows.Count
        };
    }

    private static TabularExportDocument BuildSettlementSheet(PartnershipStatement statement)
    {
        var rows = statement.Settlements
            .Select(s => new TabularExportRow([
                TabularExportCell.Date(s.SettlementDate),
                TabularExportCell.Text(s.FromPartnerName),
                TabularExportCell.Text(s.ToPartnerName),
                TabularExportCell.Number(s.Amount),
                TabularExportCell.Text(s.Currency),
                TabularExportCell.Text(s.ContractLabel ?? s.Reference ?? "—"),
                TabularExportCell.Text(s.Description ?? "—"),
                TabularExportCell.Number(s.RunningBalanceAfterUsd),
                TabularExportCell.Text(s.IsReversed ? "برگشت‌خورده" : "فعال")
            ]))
            .ToList();

        return new TabularExportDocument
        {
            FileNameStem = "partnership-settlements",
            TitleFa = "تسویه حساب شراکت",
            TitleEn = "Partnership settlements",
            Columns =
            [
                new TabularExportColumn("تاریخ", "Date", TabularExportValueType.Date, 14),
                new TabularExportColumn("از شریک", "From partner", TabularExportValueType.Text, 24),
                new TabularExportColumn("به شریک", "To partner", TabularExportValueType.Text, 24),
                new TabularExportColumn("مبلغ", "Amount", TabularExportValueType.Number, 18),
                new TabularExportColumn("ارز", "Currency", TabularExportValueType.Text, 10),
                new TabularExportColumn("قرارداد / مرجع", "Contract / reference", TabularExportValueType.Text, 26),
                new TabularExportColumn("توضیح", "Description", TabularExportValueType.Text, 30, Wrap: true),
                new TabularExportColumn("مانده پس از تسویه", "Balance after", TabularExportValueType.Number, 20),
                new TabularExportColumn("وضعیت", "Status", TabularExportValueType.Text, 14)
            ],
            Rows = rows,
            Filters = BuildFilters(statement),
            KnownRowCount = rows.Count
        };
    }
}
