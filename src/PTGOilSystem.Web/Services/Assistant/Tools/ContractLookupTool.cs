using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// خلاصهٔ قرارداد: طرف قرارداد، محصول، مقدار، وضعیت و ارز. فقط خواندنی.
/// قیمت‌گذاری محاسبه‌شده اینجا بازتولید نمی‌شود؛ فقط مقادیر ثبت‌شدهٔ خود قرارداد
/// برگردانده می‌شود تا هیچ فرمول قیمتی موازی ساخته نشود.
/// </summary>
public sealed class ContractLookupTool : IAssistantTool
{
    private readonly ApplicationDbContext _db;
    private readonly AssistantOptions _options;

    public ContractLookupTool(ApplicationDbContext db, IOptions<AssistantOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public string Name => "get_contracts";

    public string Description =>
        "فهرست یا جزئیات قراردادها: شماره، نام، نوع (خرید/فروش)، طرف قرارداد، محصول، مقدار قراردادی و وضعیت. "
        + "برای سؤال «چند قرارداد داریم؟»، «قرارداد فلانی چه وضعیتی دارد؟» یا یافتن شناسه قرارداد استفاده شود.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "search": { "type": "string", "description": "بخشی از شماره یا نام قرارداد. اختیاری." },
            "contract_id": {
              "type": ["integer", "string"],
              "description": "شناسه عددی دقیق قرارداد. فقط اگر شناسه را از نتیجهٔ قبلی گرفته‌ای بفرست؛ شناسه را از خودت نساز. برای جستجو با نام از search استفاده کن."
            },
            "only_active": { "type": "boolean", "description": "فقط قراردادهای فعال/جاری. پیش‌فرض false." }
          }
        }
        """;

    public string RequiredController => "Contracts";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var search = AssistantToolArgs.GetString(arguments, "search")?.Trim();
        var contractId = AssistantToolArgs.GetInt(arguments, "contract_id");

        var query = _db.Contracts.AsNoTracking()
            .Include(contract => contract.Product)
            .Include(contract => contract.Supplier)
            .Include(contract => contract.Customer)
            .AsQueryable();

        if (contractId.HasValue)
        {
            query = query.Where(contract => contract.Id == contractId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(contract =>
                EF.Functions.ILike(contract.ContractNumber, $"%{search}%")
                || EF.Functions.ILike(contract.ContractName, $"%{search}%"));
        }

        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(contract => contract.ContractDate)
            .Take(take)
            .Select(contract => new
            {
                contract.Id,
                contract.ContractNumber,
                contract.ContractName,
                contract.ContractType,
                contract.Status,
                contract.QuantityMt,
                contract.Currency,
                contract.ContractDate,
                ProductName = contract.Product != null ? contract.Product.Name : null,
                SupplierName = contract.Supplier != null ? contract.Supplier.Name : null,
                CustomerName = contract.Customer != null ? contract.Customer.Name : null,
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return "هیچ قراردادی با این مشخصات یافت نشد.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{total} قرارداد یافت شد (نمایش {rows.Count} مورد):");

        foreach (var row in rows)
        {
            var counterparty = row.SupplierName ?? row.CustomerName ?? "-";
            builder.Append("شناسه=").Append(row.Id)
                .Append(" | شماره=").Append(row.ContractNumber)
                .Append(" | نام=").Append(row.ContractName)
                .Append(" | نوع=").Append(row.ContractType)
                .Append(" | وضعیت=").Append(row.Status)
                .Append(" | طرف=").Append(counterparty)
                .Append(" | محصول=").Append(row.ProductName ?? "-")
                .Append(" | مقدار=").Append(row.QuantityMt.ToString("N3", CultureInfo.InvariantCulture)).Append(" MT")
                .Append(" | ارز=").Append(row.Currency)
                .Append(" | تاریخ=").Append(row.ContractDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        if (total > rows.Count)
        {
            builder.AppendLine($"({total - rows.Count} قرارداد دیگر نمایش داده نشد.)");
        }

        return builder.ToString();
    }
}
