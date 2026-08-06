using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// مانده اشخاص. عمداً روی <see cref="IPartyBalanceReadService"/> سوار است — همان
/// سرویسی که گزارش مانده مدیریتی از آن می‌خواند — تا عدد دستیار با عدد گزارش یکی
/// باشد و هیچ فرمول موازی ساخته نشود.
/// </summary>
public sealed class PartyBalanceTool : IAssistantTool
{
    private readonly IPartyBalanceReadService _balances;
    private readonly AssistantOptions _options;

    public PartyBalanceTool(IPartyBalanceReadService balances, IOptions<AssistantOptions> options)
    {
        _balances = balances;
        _options = options.Value;
    }

    public string Name => "get_party_balance";

    public string Description =>
        "مانده حساب اشخاص به دلار. اگر customer_id یا supplier_id داده شود فقط همان شخص، "
        + "وگرنه فهرست مانده همه اشخاص. مبلغ مثبت و منفی معنی جهت دارد که در ستون «معنی» توضیح داده می‌شود. "
        + "برای گرفتن شناسه ابتدا search_party را صدا بزن.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "customer_id": {
              "type": ["integer", "string"],
              "description": "شناسه عددی مشتری که search_party برگردانده است. شناسه را از خودت نساز؛ اگر آن را از search_party نگرفته‌ای این فیلد را حذف کن."
            },
            "supplier_id": {
              "type": ["integer", "string"],
              "description": "شناسه عددی تأمین‌کننده که search_party برگردانده است. حدس زدن ممنوع؛ در شک حذف کن."
            },
            "from_date": { "type": "string", "description": "تاریخ شروع دوره به شکل YYYY-MM-DD. اختیاری." },
            "to_date": { "type": "string", "description": "تاریخ پایان دوره به شکل YYYY-MM-DD. اختیاری." }
          }
        }
        """;

    /// <summary>مانده اشخاص در بخش «روزنامچه و حواله‌ها» دیده می‌شود.</summary>
    public string RequiredController => "PartyStatements";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var filter = new ManagementReportFilterViewModel
        {
            CustomerId = AssistantToolArgs.GetInt(arguments, "customer_id"),
            SupplierId = AssistantToolArgs.GetInt(arguments, "supplier_id"),
            FromDate = AssistantToolArgs.GetDate(arguments, "from_date"),
            ToDate = AssistantToolArgs.GetDate(arguments, "to_date"),
        };

        var snapshots = await _balances.GetBalancesAsync(filter, cancellationToken);
        if (snapshots.Count == 0)
        {
            return "برای این فیلتر هیچ مانده‌ای ثبت نشده است.";
        }

        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        var rows = snapshots
            .OrderByDescending(row => Math.Abs(row.ClosingBalanceUsd))
            .Take(take)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"مانده {snapshots.Count} شخص (نمایش {rows.Count} مورد، همه به دلار):");

        foreach (var row in rows)
        {
            builder.Append(PartyTypeLabel(row.PartyType))
                .Append(" | ").Append(row.PartyName)
                .Append(" | مانده=").Append(Money(row.ClosingBalanceUsd))
                .Append(" | معنی=").Append(row.BalanceMeaning);

            if (row.LastEntryDate.HasValue)
            {
                builder.Append(" | آخرین ثبت=").Append(row.LastEntryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            builder.AppendLine();
        }

        if (snapshots.Count > rows.Count)
        {
            builder.AppendLine($"({snapshots.Count - rows.Count} مورد دیگر نمایش داده نشد. برای فهرست کامل کاربر به گزارش مانده برود.)");
        }

        return builder.ToString();
    }

    internal static string PartyTypeLabel(PartyStatementPartyType type) => type switch
    {
        PartyStatementPartyType.Customer => "مشتری",
        PartyStatementPartyType.Supplier => "تأمین‌کننده",
        PartyStatementPartyType.ServiceProvider => "ارائه‌دهنده خدمات",
        PartyStatementPartyType.Sarraf => "صراف",
        PartyStatementPartyType.Employee => "کارمند",
        PartyStatementPartyType.Partner => "شریک",
        PartyStatementPartyType.Driver => "راننده",
        PartyStatementPartyType.Company => "شرکت",
        _ => "شخص",
    };

    internal static string Money(decimal value)
        => value.ToString("N2", CultureInfo.InvariantCulture);
}
