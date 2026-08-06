using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// یافتن شناسهٔ یک شخص از روی نام. مدل شناسه نمی‌داند، پس قبل از هر Tool دیگری
/// باید نام را به شناسه تبدیل کند. فقط نام و نوع برمی‌گردد؛ هیچ مبلغی اینجا نیست.
/// </summary>
public sealed class SearchPartyTool : IAssistantTool
{
    private readonly ApplicationDbContext _db;
    private readonly AssistantOptions _options;

    public SearchPartyTool(ApplicationDbContext db, IOptions<AssistantOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public string Name => "search_party";

    public string Description =>
        "جستجوی شخص (مشتری، تأمین‌کننده، صراف، کارمند، شریک، ارائه‌دهنده خدمات) بر اساس بخشی از نام. "
        + "خروجی شناسه و نوع شخص است. پیش از فراخوانی get_party_balance یا get_party_statement حتماً این را صدا بزن تا شناسه به دست بیاید.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "بخشی از نام شخص، مثلاً «احمد» یا «شرکت نور»."
            },
            "party_type": {
              "type": "string",
              "enum": ["customer", "supplier", "sarraf", "employee", "partner", "service_provider"],
              "description": "اختیاری. اگر نوع شخص معلوم است، جستجو را محدود می‌کند."
            }
          },
          "required": ["name"]
        }
        """;

    /// <summary>اشخاص زیر ناوبری «اشخاص» دیده می‌شوند.</summary>
    public string RequiredController => "Customers";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var name = AssistantToolArgs.GetString(arguments, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
        {
            return "برای جستجو حداقل دو حرف از نام لازم است.";
        }

        var wanted = AssistantToolArgs.GetString(arguments, "party_type")?.Trim().ToLowerInvariant();
        var take = Math.Clamp(_options.MaxToolRows, 5, 50);
        var results = new List<string>();

        // فیلتر و مرتب‌سازی باید روی خودِ موجودیت بنشینند، نه روی نتیجهٔ Select:
        // ILike و OrderBy روی یک نوع ساختگی به SQL ترجمه نمی‌شوند و کل جستجو با
        // استثنا می‌افتاد — یعنی دستیار هیچ‌وقت شناسهٔ شخص را پیدا نمی‌کرد.
        var pattern = $"%{name}%";

        async Task AddAsync(string type, string label, IQueryable<PartyHit> query)
        {
            if (wanted is not null && !string.Equals(wanted, type, StringComparison.Ordinal))
            {
                return;
            }

            var rows = await query.ToListAsync(cancellationToken);

            results.AddRange(rows.Select(row =>
                $"{label} | شناسه={row.Id} | نام={row.Name}{(row.IsActive ? "" : " | غیرفعال")}"));
        }

        await AddAsync("customer", "مشتری",
            _db.Customers.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern))
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.Name, x.IsActive)));
        await AddAsync("supplier", "تأمین‌کننده",
            _db.Suppliers.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern))
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.Name, x.IsActive)));
        await AddAsync("sarraf", "صراف",
            _db.Sarrafs.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern))
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.Name, x.IsActive)));
        await AddAsync("partner", "شریک",
            _db.Partners.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern))
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.Name, x.IsActive)));
        await AddAsync("service_provider", "ارائه‌دهنده خدمات",
            _db.Set<Models.Entities.ServiceProvider>().AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern))
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.Name, x.IsActive)));
        await AddAsync("employee", "کارمند",
            _db.Employees.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.FullName, pattern))
                .OrderBy(x => x.FullName)
                .Take(take)
                .Select(x => new PartyHit(x.Id, x.FullName, x.IsActive)));

        if (results.Count == 0)
        {
            return $"هیچ شخصی با نام «{name}» یافت نشد.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{results.Count} نتیجه برای «{name}»:");
        foreach (var row in results.Take(take))
        {
            builder.AppendLine(row);
        }

        return builder.ToString();
    }

    private sealed record PartyHit(int Id, string Name, bool IsActive);
}
