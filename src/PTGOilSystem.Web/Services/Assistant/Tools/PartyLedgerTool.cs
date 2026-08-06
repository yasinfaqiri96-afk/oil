using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// خلاصهٔ صورتحساب یک شخص: مانده اول دوره، جمع دریافت، جمع پرداخت، مانده پایان
/// دوره و آخرین اسناد.
///
/// روی <see cref="IPartyStatementReadService"/> سوار است — همان سرویسی که خودِ
/// صفحهٔ صورتحساب از آن می‌خواند — پس همهٔ جمع‌ها را سیستم حساب می‌کند و دستیار
/// فقط همان اعداد را می‌خواند. مدل هیچ جمع یا تفریقی انجام نمی‌دهد.
/// </summary>
public sealed class PartyLedgerTool : IAssistantTool
{
    private readonly IPartyStatementReadService _statements;
    private readonly AssistantOptions _options;

    public PartyLedgerTool(IPartyStatementReadService statements, IOptions<AssistantOptions> options)
    {
        _statements = statements;
        _options = options.Value;
    }

    public string Name => "get_party_ledger";

    public string Description =>
        "خلاصهٔ حساب یک شخص: مانده اول دوره، جمع دریافت‌ها، جمع پرداخت‌ها، مانده نهایی و آخرین اسناد. "
        + "برای «چقدر پرداخت شده؟»، «چقدر بدهکار است؟» یا «حساب فلانی را بررسی کن» استفاده شود. "
        + "شناسه را اول با search_party بگیر.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "supplier_id": { "type": ["integer", "string"], "description": "شناسه تأمین‌کننده از search_party. شناسه را از خودت نساز." },
            "customer_id": { "type": ["integer", "string"], "description": "شناسه مشتری از search_party. شناسه را از خودت نساز." },
            "contract_id": { "type": ["integer", "string"], "description": "فقط اسناد همین قرارداد. اختیاری." },
            "from_date": { "type": "string", "description": "تاریخ شروع دوره YYYY-MM-DD. اختیاری." },
            "to_date": { "type": "string", "description": "تاریخ پایان دوره YYYY-MM-DD. اختیاری." }
          }
        }
        """;

    /// <summary>صورتحساب اشخاص در بخش «روزنامچه و حواله‌ها» دیده می‌شود.</summary>
    public string RequiredController => "PartyStatements";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var supplierId = AssistantToolArgs.GetInt(arguments, "supplier_id");
        var customerId = AssistantToolArgs.GetInt(arguments, "customer_id");

        PartyRef party;
        if (supplierId is > 0)
        {
            party = new PartyRef(PartyStatementPartyType.Supplier, supplierId.Value);
        }
        else if (customerId is > 0)
        {
            party = new PartyRef(PartyStatementPartyType.Customer, customerId.Value);
        }
        else
        {
            return "برای این ابزار شناسه تأمین‌کننده یا مشتری لازم است. اول search_party را صدا بزن.";
        }

        var filter = new PartyStatementFilter
        {
            FromDate = AssistantToolArgs.GetDate(arguments, "from_date"),
            ToDate = AssistantToolArgs.GetDate(arguments, "to_date"),
            ContractId = AssistantToolArgs.GetInt(arguments, "contract_id"),
            Page = 1,
            PageSize = Math.Clamp(_options.MaxToolRows, 5, 50),
        };

        var statement = await _statements.GetStatementAsync(party, filter, cancellationToken);
        var summary = statement.Summary;

        var builder = new StringBuilder();
        builder.Append(PartyBalanceTool.PartyTypeLabel(statement.Party.PartyType))
            .Append(" شناسه=").Append(statement.Party.PartyId)
            .Append(" | نام=").Append(statement.PartyInfo.Name)
            .AppendLine();

        builder.Append("ارز پایه=").Append(summary.BaseCurrencyCode)
            .Append(" | مانده اول دوره=").Append(Money(summary.OpeningBalance))
            .Append(" | جمع دریافت=").Append(Money(summary.TotalReceipt))
            .Append(" | جمع پرداخت=").Append(Money(summary.TotalOutflow))
            .Append(" | مانده نهایی=").Append(Money(summary.ClosingBalance))
            .Append(" | معنی=").Append(summary.ClosingBalanceMeaning)
            .AppendLine();

        if (statement.Rows.Count == 0)
        {
            builder.AppendLine("در این دوره هیچ سندی ثبت نشده است.");
            return builder.ToString();
        }

        builder.AppendLine($"آخرین اسناد (نمایش {statement.Rows.Count} مورد):");
        foreach (var row in statement.Rows.Take(filter.PageSize))
        {
            builder.Append("  - تاریخ=").Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" | شرح=").Append(Trim(row.Description, 80))
                .Append(" | رسید=").Append(Money(row.ReceiptBase))
                .Append(" | برد=").Append(Money(row.OutflowBase))
                .Append(" | مانده=").Append(Money(row.RunningBalance))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Money(decimal? value) => value.HasValue ? Money(value.Value) : "-";

    private static string Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Length <= max ? value : value[..max];
}
