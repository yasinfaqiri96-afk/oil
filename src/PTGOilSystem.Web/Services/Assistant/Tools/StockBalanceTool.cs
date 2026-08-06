using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// موجودی آزاد انبار. روی <see cref="IStockService"/> سوار است تا همان قاعدهٔ
/// رسمی سیستم رعایت شود: موجودی فقط از InventoryMovement حساب می‌شود و هیچ فیلد
/// دستی خوانده نمی‌شود.
/// </summary>
public sealed class StockBalanceTool : IAssistantTool
{
    private readonly IStockService _stock;
    private readonly ApplicationDbContext _db;
    private readonly AssistantOptions _options;

    public StockBalanceTool(IStockService stock, ApplicationDbContext db, IOptions<AssistantOptions> options)
    {
        _stock = stock;
        _db = db;
        _options = options.Value;
    }

    public string Name => "get_stock_balance";

    public string Description =>
        "موجودی آزاد فعلی انبار به تن متریک (MT)، تفکیک‌شده بر اساس محصول و ترمینال. "
        + "برای سؤال‌هایی مثل «چقدر موجودی داریم؟» یا «موجودی فلان محصول چند است؟» استفاده شود. "
        + "برای موجودی کل، بدون هیچ ورودی صدا بزن.";

    // عمداً هیچ فیلد شناسه‌ای اینجا نیست و فقط نام پذیرفته می‌شود.
    // در آزمایش واقعی، مدل برای این سؤال‌ها شناسهٔ عددیِ ساختگی تولید می‌کرد و نتیجه
    // یا خطای اعتبارسنجیِ سرویس بود یا «موجودی صفر»ِ گمراه‌کننده. وقتی شناسه‌ای در
    // Schema نباشد، چیزی برای ساختن هم نمی‌ماند؛ تبدیل نام به شناسه اینجا و روی
    // داده واقعی انجام می‌شود.
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "product_name": {
              "type": "string",
              "description": "نام یا بخشی از نام محصول، مثلاً «دیزل». اگر کاربر محصول مشخصی را نام نبرده، این فیلد را کاملاً حذف کن تا موجودی کل برگردد."
            },
            "terminal_name": {
              "type": "string",
              "description": "نام یا بخشی از نام ترمینال یا انبار. اگر کاربر انبار مشخصی را نام نبرده، این فیلد را کاملاً حذف کن."
            }
          }
        }
        """;

    public string RequiredController => "Inventory";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // نام‌ها روی داده واقعی به شناسه تبدیل می‌شوند. نام ناموجود صریحاً گزارش
        // می‌شود تا مدل آن را با «موجودی صفر» اشتباه نگیرد.
        var productName = AssistantToolArgs.GetString(arguments, "product_name")?.Trim();
        int? productId = null;
        if (!string.IsNullOrWhiteSpace(productName))
        {
            var matches = await _db.Products.AsNoTracking()
                .Where(product => EF.Functions.ILike(product.Name, $"%{productName}%"))
                .Select(product => new { product.Id, product.Name })
                .Take(5)
                .ToListAsync(cancellationToken);

            if (matches.Count == 0)
            {
                return $"محصولی به نام «{productName}» ثبت نشده است. "
                    + "این یعنی نام اشتباه است، نه اینکه موجودی صفر باشد. به کاربر همین را بگو.";
            }

            if (matches.Count > 1)
            {
                return "چند محصول با این نام هست: "
                    + string.Join("، ", matches.Select(match => match.Name))
                    + ". از کاربر بپرس کدام را می‌خواهد.";
            }

            productId = matches[0].Id;
        }

        var terminalName = AssistantToolArgs.GetString(arguments, "terminal_name")?.Trim();
        int? terminalId = null;
        if (!string.IsNullOrWhiteSpace(terminalName))
        {
            var matches = await _db.Terminals.AsNoTracking()
                .Where(terminal => EF.Functions.ILike(terminal.Name, $"%{terminalName}%"))
                .Select(terminal => new { terminal.Id, terminal.Name })
                .Take(5)
                .ToListAsync(cancellationToken);

            if (matches.Count == 0)
            {
                return $"ترمینالی به نام «{terminalName}» ثبت نشده است. "
                    + "این یعنی نام اشتباه است، نه اینکه موجودی صفر باشد. به کاربر همین را بگو.";
            }

            if (matches.Count > 1)
            {
                return "چند ترمینال با این نام هست: "
                    + string.Join("، ", matches.Select(match => match.Name))
                    + ". از کاربر بپرس کدام را می‌خواهد.";
            }

            terminalId = matches[0].Id;
        }

        var items = await _stock.GetStockSummaryAsync(
            productId: productId,
            contractId: null,
            terminalId: terminalId,
            asOfUtc: null,
            ct: cancellationToken);

        var positive = items.Where(item => item.FreeQuantityMt != 0).ToList();
        if (positive.Count == 0)
        {
            // نام‌ها بالاتر اعتبارسنجی شده‌اند، پس اینجا واقعاً یعنی موجودی آزادی
            // ثبت نشده — نه اینکه فیلتر اشتباه بوده باشد.
            var scope = productId.HasValue || terminalId.HasValue ? "برای این فیلتر" : "در سیستم";
            return $"هیچ موجودی آزادی {scope} ثبت نشده است.";
        }

        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        var rows = positive
            .OrderByDescending(item => item.FreeQuantityMt)
            .Take(take)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"موجودی آزاد (مجموع {positive.Sum(i => i.FreeQuantityMt):N3} MT در {positive.Count} ردیف):");

        foreach (var item in rows)
        {
            builder.Append(item.ProductName)
                .Append(" | ترمینال=").Append(item.TerminalName)
                .Append(" | آزاد=").Append(item.FreeQuantityMt.ToString("N3", CultureInfo.InvariantCulture)).Append(" MT");

            if (!string.IsNullOrWhiteSpace(item.ContractNumber))
            {
                builder.Append(" | قرارداد=").Append(item.ContractNumber);
            }

            builder.Append(" | آخرین حرکت=")
                .Append(item.LastMovementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        if (positive.Count > rows.Count)
        {
            builder.AppendLine($"({positive.Count - rows.Count} ردیف دیگر نمایش داده نشد.)");
        }

        return builder.ToString();
    }
}
