using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// خروجی Excel/PDF صورت‌حساب شریک. همان ارقام و همان فیلترهای صفحهٔ پروفایل را می‌نویسد —
/// از همان <see cref="IPartnershipStatementService"/> — و هیچ محاسبهٔ مالی تازه‌ای ندارد.
/// </summary>
public partial class PartnersController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(
        int id,
        int? contractId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? format = null,
        CancellationToken ct = default)
    {
        var exists = await _db.Partners.AsNoTracking().AnyAsync(x => x.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var statement = await _partnershipStatements.BuildForPartnerAsync(id, contractIds: null, ct);
        if (statement is null)
        {
            return NotFound();
        }

        // همان فیلترِ نمایشیِ صفحه: مانده تجمعی دست‌نخورده می‌ماند و فقط ردیف‌ها کم می‌شوند.
        var entries = statement.Entries.AsEnumerable();
        if (contractId.HasValue)
        {
            entries = entries.Where(e => e.ContractId == contractId.Value);
        }

        if (fromDate.HasValue)
        {
            var from = fromDate.Value.Date;
            entries = entries.Where(e => e.Date.HasValue && e.Date.Value.Date >= from);
        }

        if (toDate.HasValue)
        {
            var to = toDate.Value.Date;
            entries = entries.Where(e => e.Date.HasValue && e.Date.Value.Date <= to);
        }

        var filters = TabularExportSupport.FilterSummary(
            ("شریک", statement.PartnerName),
            ("قرارداد", contractId.HasValue
                ? statement.ContractOptions.FirstOrDefault(o => o.ContractId == contractId.Value)?.ContractLabel
                : null),
            ("از تاریخ", fromDate?.ToString("yyyy-MM-dd")),
            ("تا تاریخ", toDate?.ToString("yyyy-MM-dd")));

        var sheets = new List<TabularExportDocument>
        {
            BuildPartnerSummarySheet(statement, filters),
            BuildPartnerLedgerSheet(statement, entries.ToList(), filters)
        };

        return TabularExportSupport.File(this, format, sheets);
    }

    private static TabularExportDocument BuildPartnerSummarySheet(
        PartnerAccountStatement statement,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = statement.Contracts.Select(row => new TabularExportRow(
        [
            TabularExportCell.Text(row.ContractLabel),
            TabularExportCell.Number(row.FundingUsd),
            TabularExportCell.Number(row.ProceedsHeldUsd),
            TabularExportCell.Number(row.ProfitShareUsd),
            TabularExportCell.Number(row.NetPositionUsd)
        ])).ToList();

        return new TabularExportDocument
        {
            FileNameStem = $"PTG_Partner_Statement_{statement.PartnerId}",
            TitleFa = $"خلاصه حساب شریک — {statement.PartnerName}",
            TitleEn = $"Partner account summary — {statement.PartnerName}",
            Columns =
            [
                new TabularExportColumn("قرارداد", "Contract", TabularExportValueType.Text, 34, Wrap: true),
                new TabularExportColumn("پرداخت شریک", "Partner funding", TabularExportValueType.Number, 18),
                new TabularExportColumn("عاید نزد شریک", "Proceeds held", TabularExportValueType.Number, 18),
                new TabularExportColumn("سهم مفاد/ضرر", "Profit share", TabularExportValueType.Number, 18),
                new TabularExportColumn("اثر بر حساب شریک", "Net position", TabularExportValueType.Number, 18)
            ],
            Rows = rows,
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text("جمع"),
                TabularExportCell.Number(statement.FundingUsd),
                TabularExportCell.Number(statement.ProceedsHeldUsd),
                TabularExportCell.Number(statement.ProfitShareUsd),
                TabularExportCell.Number(statement.NetPositionUsd)
            ]),
            Filters = filters,
            KnownRowCount = rows.Count
        };
    }

    // متن بلند در Excel روی ستون‌های بعدی می‌افتد، پس شرح در همان عرض ستون خلاصه می‌شود.
    private const int PartnerDescriptionMaxLength = 40;

    private static string ShortenPartnerText(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static TabularExportDocument BuildPartnerLedgerSheet(
        PartnerAccountStatement statement,
        IReadOnlyList<PartnerAccountEntry> entries,
        IReadOnlyList<TabularExportFilter> filters)
    {
        var rows = entries.Select(entry => new TabularExportRow(
        [
            TabularExportCell.Date(entry.Date),
            TabularExportCell.Text(ShortenPartnerText(entry.ContractLabel ?? "تسویه عمومی", 24)),
            TabularExportCell.Text(ShortenPartnerText(entry.Description, PartnerDescriptionMaxLength)),
            TabularExportCell.Number(entry.QuantityMt),
            TabularExportCell.Number(entry.UnitPriceUsd),
            TabularExportCell.Number(entry.CreditUsd),
            TabularExportCell.Number(entry.DebitUsd),
            TabularExportCell.Number(entry.AccountantBalanceUsd)
        ])).ToList();

        return new TabularExportDocument
        {
            FileNameStem = $"PTG_Partner_Ledger_{statement.PartnerId}",
            TitleFa = $"گردش حساب شریک — {statement.PartnerName}",
            TitleEn = $"Partner ledger — {statement.PartnerName}",
            ForceLandscape = true,
            Columns =
            [
                new TabularExportColumn("تاریخ", "Date", TabularExportValueType.Date, 14),
                new TabularExportColumn("قرارداد", "Contract", TabularExportValueType.Text, 24),
                new TabularExportColumn("شرح", "Description", TabularExportValueType.Text, 44, Wrap: true),
                new TabularExportColumn("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14),
                new TabularExportColumn("نرخ", "Unit price", TabularExportValueType.Number, 14),
                new TabularExportColumn("بردگی (USD)", "Credit (USD)", TabularExportValueType.Number, 16),
                new TabularExportColumn("رسیدگی (USD)", "Debit (USD)", TabularExportValueType.Number, 16),
                new TabularExportColumn("مانده (USD)", "Balance (USD)", TabularExportValueType.Number, 16)
            ],
            Rows = rows,
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Text("جمع"),
                TabularExportCell.Text(null),
                TabularExportCell.Text(null),
                TabularExportCell.Number(statement.TotalCreditUsd),
                TabularExportCell.Number(statement.TotalDebitUsd),
                TabularExportCell.Number(statement.AccountantBalanceUsd)
            ]),
            Filters = filters,
            KnownRowCount = rows.Count
        };
    }
}
