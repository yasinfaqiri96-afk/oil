using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// قراردادهای باز: آن‌هایی که هنوز مقدار باقی‌مانده دارند، هیچ بارگیری ندارند، یا
/// مدتی است بارگیری نشده‌اند.
///
/// مقدار بارگیری‌شده از <see cref="IPurchaseAggregationService"/> می‌آید تا با
/// گزارش‌های موجود یکی باشد؛ باقی‌مانده و روزهای بی‌حرکت در همین کد حساب می‌شوند
/// و مدل هیچ عددی نمی‌سازد.
/// </summary>
public sealed class OpenContractsTool : IAssistantTool
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchases;
    private readonly AssistantOptions _options;

    public OpenContractsTool(
        ApplicationDbContext db,
        IPurchaseAggregationService purchases,
        IOptions<AssistantOptions> options)
    {
        _db = db;
        _purchases = purchases;
        _options = options.Value;
    }

    public string Name => "get_open_contracts";

    public string Description =>
        "قراردادهای باز با مقدار باقی‌مانده، همراه تاریخ آخرین بارگیری و تعداد روزهای بدون بارگیری. "
        + "برای «کدام قراردادها باز است؟»، «کدام قرارداد بارگیری نشده؟» یا «کدام قرارداد تأخیر دارد؟» استفاده شود.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "supplier_id": { "type": ["integer", "string"], "description": "فقط قراردادهای این تأمین‌کننده. شناسه را از ابزار دیگری بگیر." },
            "customer_id": { "type": ["integer", "string"], "description": "فقط قراردادهای این مشتری. شناسه را از ابزار دیگری بگیر." },
            "without_loading": { "type": "boolean", "description": "فقط قراردادهایی که هیچ بارگیری ندارند. پیش‌فرض false." },
            "stale_days": { "type": "integer", "description": "فقط قراردادهایی که بیش از این تعداد روز بارگیری نشده‌اند، مثلاً 30." }
          }
        }
        """;

    public string RequiredController => "Contracts";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var supplierId = AssistantToolArgs.GetInt(arguments, "supplier_id");
        var customerId = AssistantToolArgs.GetInt(arguments, "customer_id");
        var withoutLoading = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("without_loading", out var flag)
            && flag.ValueKind == JsonValueKind.True;
        var staleDays = AssistantToolArgs.GetInt(arguments, "stale_days");

        var query = _db.Contracts.AsNoTracking()
            .Include(contract => contract.Product)
            .Include(contract => contract.Supplier)
            .Include(contract => contract.Customer)
            .AsQueryable();

        if (supplierId is > 0)
        {
            query = query.Where(contract => contract.SupplierId == supplierId.Value);
        }

        if (customerId is > 0)
        {
            query = query.Where(contract => contract.CustomerId == customerId.Value);
        }

        var contracts = await query
            .OrderByDescending(contract => contract.ContractDate)
            .Take(200)
            .ToListAsync(cancellationToken);

        if (contracts.Count == 0)
        {
            return "قراردادی با این فیلتر ثبت نشده است.";
        }

        var contractIds = contracts.Select(contract => contract.Id).ToList();
        var finalPriceById = contracts.ToDictionary(
            contract => contract.Id,
            contract => ContractPricingAdapter.GetCanonicalFinalPrice(contract));

        var snapshots = await _purchases.AggregateForContractsAsync(contractIds, finalPriceById, cancellationToken);

        var lastLoadingById = await _db.LoadingRegisters.AsNoTracking()
            .Where(register => contractIds.Contains(register.ContractId))
            .GroupBy(register => register.ContractId)
            .Select(group => new { ContractId = group.Key, LastDate = group.Max(register => register.LoadingDate), Count = group.Count() })
            .ToDictionaryAsync(row => row.ContractId, cancellationToken);

        var today = DateTime.Today;
        var rows = new List<(int Id, string Number, string Party, decimal Quantity, decimal Loaded, decimal Remaining, DateTime? Last, int Count, int IdleDays)>();

        foreach (var contract in contracts)
        {
            var loaded = snapshots.TryGetValue(contract.Id, out var snapshot) ? snapshot.TotalLoadedQuantityMt : 0m;
            var remaining = contract.QuantityMt - loaded;
            lastLoadingById.TryGetValue(contract.Id, out var lastLoading);

            var count = lastLoading?.Count ?? 0;
            var last = lastLoading?.LastDate;
            var idleDays = last.HasValue ? (int)(today - last.Value.Date).TotalDays : int.MaxValue;

            // قرارداد بسته (باقی‌مانده صفر یا منفی) در فهرست «باز» نمی‌آید.
            if (remaining <= 0m)
            {
                continue;
            }

            if (withoutLoading && count > 0)
            {
                continue;
            }

            if (staleDays is > 0 && idleDays < staleDays.Value)
            {
                continue;
            }

            rows.Add((
                contract.Id,
                contract.ContractNumber,
                contract.Supplier?.Name ?? contract.Customer?.Name ?? "-",
                contract.QuantityMt,
                loaded,
                remaining,
                last,
                count,
                idleDays));
        }

        if (rows.Count == 0)
        {
            return "هیچ قرارداد بازی با این شرط یافت نشد.";
        }

        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        var shown = rows
            .OrderByDescending(row => row.Remaining)
            .Take(take)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"{rows.Count} قرارداد باز (نمایش {shown.Count} مورد):");

        foreach (var row in shown)
        {
            builder.Append("شناسه=").Append(row.Id)
                .Append(" | شماره=").Append(row.Number)
                .Append(" | طرف=").Append(row.Party)
                .Append(" | مقدار=").Append(Qty(row.Quantity)).Append(" MT")
                .Append(" | بارگیری‌شده=").Append(Qty(row.Loaded)).Append(" MT")
                .Append(" | باقی‌مانده=").Append(Qty(row.Remaining)).Append(" MT")
                .Append(" | تعداد بارگیری=").Append(row.Count)
                .Append(" | آخرین بارگیری=")
                .Append(row.Last.HasValue ? row.Last.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "هیچ")
                .Append(" | روزهای بدون بارگیری=")
                .Append(row.IdleDays == int.MaxValue ? "-" : row.IdleDays.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        if (rows.Count > shown.Count)
        {
            builder.AppendLine($"({rows.Count - shown.Count} قرارداد دیگر نمایش داده نشد.)");
        }

        return builder.ToString();
    }

    private static string Qty(decimal value) => value.ToString("N3", CultureInfo.InvariantCulture);
}
