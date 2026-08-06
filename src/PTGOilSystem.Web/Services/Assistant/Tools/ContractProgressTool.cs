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
/// پیشرفت یک قرارداد: مقدار قراردادی، بارگیری‌شده، باقی‌مانده، تعداد و تاریخ
/// بارگیری‌ها و هزینهٔ ثبت‌شدهٔ آن‌ها.
///
/// مقدار بارگیری‌شده از <see cref="IPurchaseAggregationService"/> می‌آید — همان
/// سرویسی که پروندهٔ قرارداد، گزارشات و مغایرت‌گیری از آن می‌خوانند — تا عدد
/// دستیار با عدد گزارش یکی باشد و هیچ جمع موازی ساخته نشود. باقی‌مانده در همین
/// کد حساب می‌شود، نه در مدل.
/// </summary>
public sealed class ContractProgressTool : IAssistantTool
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchases;
    private readonly AssistantOptions _options;

    public ContractProgressTool(
        ApplicationDbContext db,
        IPurchaseAggregationService purchases,
        IOptions<AssistantOptions> options)
    {
        _db = db;
        _purchases = purchases;
        _options = options.Value;
    }

    public string Name => "get_contract_progress";

    public string Description =>
        "پیشرفت قرارداد: مقدار قراردادی، مقدار بارگیری‌شده، مقدار باقی‌مانده، تعداد بارگیری‌ها، "
        + "تاریخ اولین و آخرین بارگیری و جمع هزینهٔ بارگیری‌ها. "
        + "برای سؤال «چقدر باقی مانده؟»، «کِی بارگیری شده؟» یا «این قرارداد در چه وضعیتی است؟» استفاده شود. "
        + "اگر شناسه نداری، با search نام یا شمارهٔ قرارداد را بفرست.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "contract_id": {
              "type": ["integer", "string"],
              "description": "شناسه عددی قرارداد. فقط شناسه‌ای که از ابزار دیگری گرفته‌ای یا شناسهٔ رکورد باز در صفحه. شناسه را از خودت نساز."
            },
            "search": {
              "type": "string",
              "description": "بخشی از شماره یا نام قرارداد، وقتی شناسه در دست نیست."
            }
          }
        }
        """;

    public string RequiredController => "Contracts";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var contractId = AssistantToolArgs.GetInt(arguments, "contract_id");
        var search = AssistantToolArgs.GetString(arguments, "search")?.Trim();

        var query = _db.Contracts.AsNoTracking();
        if (contractId is > 0)
        {
            query = query.Where(contract => contract.Id == contractId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(contract =>
                EF.Functions.ILike(contract.ContractNumber, $"%{search}%")
                || EF.Functions.ILike(contract.ContractName, $"%{search}%"));
        }
        else
        {
            return "برای این ابزار شناسه یا نام قرارداد لازم است.";
        }

        var matches = await query
            .Include(contract => contract.Product)
            .Include(contract => contract.Supplier)
            .Include(contract => contract.Customer)
            .OrderByDescending(contract => contract.ContractDate)
            .Take(5)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return "قراردادی با این مشخصات یافت نشد. این یعنی شناسه یا نام اشتباه است.";
        }

        if (matches.Count > 1)
        {
            var names = matches.Select(match => $"{match.ContractNumber} (شناسه {match.Id})");
            return "چند قرارداد با این مشخصات هست: " + string.Join("، ", names) + ". از کاربر بپرس کدام را می‌خواهد.";
        }

        var contract = matches[0];

        // جمع بارگیری از سرویس رسمی خرید؛ هیچ جمع محلی‌ای اینجا ساخته نمی‌شود.
        // قیمت نهایی هم از همان آداپتوری می‌آید که بقیهٔ گزارش‌ها استفاده می‌کنند.
        var snapshot = await _purchases.AggregateForContractAsync(
            contract.Id,
            ContractPricingAdapter.GetCanonicalFinalPrice(contract),
            cancellationToken);

        var loadings = await _db.LoadingRegisters.AsNoTracking()
            .Where(register => register.ContractId == contract.Id)
            .Select(register => new
            {
                register.Id,
                register.LoadingDate,
                register.LoadedQuantityMt,
                register.WagonNumber,
                register.DestinationName,
            })
            .OrderByDescending(register => register.LoadingDate)
            .ToListAsync(cancellationToken);

        var remainingMt = contract.QuantityMt - snapshot.TotalLoadedQuantityMt;
        var progress = contract.QuantityMt > 0m
            ? snapshot.TotalLoadedQuantityMt / contract.QuantityMt * 100m
            : 0m;

        var builder = new StringBuilder();
        builder.Append("قرارداد شناسه=").Append(contract.Id)
            .Append(" | شماره=").Append(contract.ContractNumber)
            .Append(" | نام=").Append(contract.ContractName)
            .Append(" | نوع=").Append(contract.ContractType)
            .Append(" | وضعیت=").Append(contract.Status)
            .Append(" | محصول=").Append(contract.Product?.Name ?? "-")
            .Append(" | ارز=").Append(contract.Currency)
            .AppendLine();

        if (contract.SupplierId is > 0)
        {
            builder.Append("تأمین‌کننده شناسه=").Append(contract.SupplierId).Append(" | نام=").Append(contract.Supplier?.Name ?? "-").AppendLine();
        }

        if (contract.CustomerId is > 0)
        {
            builder.Append("مشتری شناسه=").Append(contract.CustomerId).Append(" | نام=").Append(contract.Customer?.Name ?? "-").AppendLine();
        }

        builder.Append("مقدار قراردادی=").Append(Qty(contract.QuantityMt)).Append(" MT")
            .Append(" | بارگیری‌شده=").Append(Qty(snapshot.TotalLoadedQuantityMt)).Append(" MT")
            .Append(" | باقی‌مانده=").Append(Qty(remainingMt)).Append(" MT")
            .Append(" | پیشرفت=").Append(progress.ToString("N1", CultureInfo.InvariantCulture)).Append('%')
            .AppendLine();

        builder.Append("تعداد بارگیری=").Append(loadings.Count);
        if (loadings.Count > 0)
        {
            builder.Append(" | اولین بارگیری=").Append(Date(loadings.Min(row => row.LoadingDate)))
                .Append(" | آخرین بارگیری=").Append(Date(loadings.Max(row => row.LoadingDate)));
        }

        builder.AppendLine();

        builder.Append("بارگیری بدون قیمت=").Append(snapshot.PendingLoadingCount)
            .Append(" | مقدار بدون قیمت=").Append(Qty(snapshot.PendingPurchaseQuantityMt)).Append(" MT")
            .Append(" | هزینهٔ حمل=").Append(Money(snapshot.LoadingTransportExpenseUsd)).Append(" USD")
            .Append(" | هزینهٔ انبار=").Append(Money(snapshot.LoadingWarehouseExpenseUsd)).Append(" USD")
            .Append(" | هزینهٔ ریلی=").Append(Money(snapshot.LoadingRailwayExpenseUsd)).Append(" USD")
            .AppendLine();

        if (snapshot.WeightedAveragePurchasePriceUsd is { } average)
        {
            builder.Append("میانگین وزنی قیمت خرید=").Append(Money(average)).AppendLine(" USD/MT");
        }

        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        if (loadings.Count > 0)
        {
            builder.AppendLine($"آخرین بارگیری‌ها (نمایش {Math.Min(take, loadings.Count)} از {loadings.Count}):");
            foreach (var row in loadings.Take(take))
            {
                builder.Append("  - شناسه=").Append(row.Id)
                    .Append(" | تاریخ=").Append(Date(row.LoadingDate))
                    .Append(" | مقدار=").Append(Qty(row.LoadedQuantityMt)).Append(" MT")
                    .Append(" | واگن=").Append(row.WagonNumber ?? "-")
                    .Append(" | مقصد=").Append(row.DestinationName ?? "-")
                    .AppendLine();
            }
        }
        else
        {
            builder.AppendLine("برای این قرارداد هنوز هیچ بارگیری ثبت نشده است.");
        }

        return builder.ToString();
    }

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("N3", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
