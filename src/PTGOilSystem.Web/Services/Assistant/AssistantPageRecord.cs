using System.Globalization;
using System.Text.RegularExpressions;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>رکوردی که صفحهٔ جاری دربارهٔ آن است: نوع، شناسه و نام ورودی ابزار.</summary>
/// <param name="Kind">نام فارسی نوع رکورد، برای پیام مدل.</param>
/// <param name="Id">شناسهٔ عددی همان رکورد.</param>
/// <param name="ToolArgument">نام ورودی ابزار که این شناسه باید در آن بنشیند.</param>
public readonly record struct AssistantPageRecord(string Kind, int Id, string ToolArgument);

/// <summary>
/// تشخیص «همین رکورد» از روی مسیر صفحهٔ جاری.
///
/// چرا در Backend: مسیر از Frontend می‌آید و بی‌اعتماد است، پس فقط یک عدد از آن
/// بیرون کشیده می‌شود و هیچ داده‌ای با آن خوانده نمی‌شود. خواندن واقعی همیشه از
/// راه Tool انجام می‌شود و همان‌جا دسترسی کاربر دوباره سنجیده می‌گردد — شناسه‌ای
/// که کاربر اجازهٔ دیدنش را ندارد، اینجا هم چیزی را باز نمی‌کند.
///
/// بدون این، سؤال «همین بارگیری را بررسی کن» به مدل می‌رسید بی‌آنکه بداند «همین»
/// کدام است، و مدل شناسه از خودش می‌ساخت.
/// </summary>
public static class AssistantPageRecordResolver
{
    private static readonly Regex TrailingId = new(@"/(\d{1,9})(?:/|$)", RegexOptions.Compiled);

    /// <summary>نگاشت Controller به نوع رکورد و ورودی ابزار مربوط.</summary>
    private static readonly Dictionary<string, (string Kind, string ToolArgument)> ByController =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Loading"] = ("بارگیری", "loading_id"),
            ["LoadingReceipts"] = ("رسید بارگیری", "loading_id"),
            ["Contracts"] = ("قرارداد", "contract_id"),
            ["ContractJourney"] = ("قرارداد", "contract_id"),
            ["ContractAmendments"] = ("قرارداد", "contract_id"),
            ["Suppliers"] = ("تأمین‌کننده", "supplier_id"),
            ["SupplierBalanceTransfers"] = ("تأمین‌کننده", "supplier_id"),
            ["Customers"] = ("مشتری", "customer_id"),
        };

    /// <summary>نام پارامترهای Query که در این برنامه شناسهٔ رکورد را حمل می‌کنند.</summary>
    private static readonly (string Key, string Kind, string ToolArgument)[] QueryKeys =
    {
        ("loadingId", "بارگیری", "loading_id"),
        ("loadingRegisterId", "بارگیری", "loading_id"),
        ("contractId", "قرارداد", "contract_id"),
        ("supplierId", "تأمین‌کننده", "supplier_id"),
        ("customerId", "مشتری", "customer_id"),
    };

    public static AssistantPageRecord? Resolve(AssistantPageContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var route = context.Route ?? string.Empty;
        var separator = route.IndexOf('?');
        var path = separator >= 0 ? route[..separator] : route;
        var query = separator >= 0 ? route[(separator + 1)..] : string.Empty;

        // Query صریح‌تر از مسیر است: /SupplierBalanceTransfers/History?supplierId=4
        // شناسه را در Query دارد و در مسیر هیچ عددی نیست.
        foreach (var (key, kind, argument) in QueryKeys)
        {
            var value = QueryValue(query, key);
            if (value is > 0)
            {
                return new AssistantPageRecord(kind, value.Value, argument);
            }
        }

        var match = TrailingId.Match(path);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        // نوع از Controller می‌آید؛ اگر Controller شناخته نشد، عدد بی‌نام است و
        // بهتر است اصلاً به مدل داده نشود تا شناسهٔ بی‌ربط به ابزار نرود.
        var controller = context.Controller ?? ControllerFromPath(path);
        if (controller is null || !ByController.TryGetValue(controller, out var mapped))
        {
            return null;
        }

        return new AssistantPageRecord(mapped.Kind, id, mapped.ToolArgument);
    }

    private static string? ControllerFromPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : null;
    }

    private static int? QueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            if (!string.Equals(pair[..equals], key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(pair[(equals + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
