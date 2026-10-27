using System.Text.RegularExpressions;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// برچسب کوتاهِ «شماره سند» حرکت موجودی برای فهرست‌ها. فقط نمایشی است: مقدار ذخیره‌شده
/// دست نمی‌خورد و متن کامل در tooltip همان سلول می‌ماند.
/// مرجع سیستمی (مثل LOADING-RECEIPT:12:guid یا BULK-RCPT-2-… / 1383461) به «نوع #شناسه»
/// یا شمارهٔ وسیله خلاصه می‌شود؛ مرجعی که کاربر نوشته عیناً نمایش داده می‌شود.
/// </summary>
public static partial class MovementReferenceDisplay
{
    private const string CancelSuffix = "-CANCEL";
    private const string BulkReceiptPrefix = "BULK-RCPT-";

    public static string Short(string? reference, Func<string, string, string> t)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return "—";
        }

        var text = reference.Trim();
        var cancelled = text.EndsWith(CancelSuffix, StringComparison.OrdinalIgnoreCase);
        if (cancelled)
        {
            text = text[..^CancelSuffix.Length].TrimEnd();
        }

        var label = ShortCore(text, t);
        return cancelled ? $"{label} ({t("لغو", "reversal")})" : label;
    }

    private static string ShortCore(string text, Func<string, string, string> t)
    {
        if (text.StartsWith(BulkReceiptPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // «BULK-RCPT-{contract}-{timestamp} / {vehicle}»: شمارهٔ وسیله همان چیزی است که کاربر می‌شناسد.
            var slash = text.LastIndexOf(" / ", StringComparison.Ordinal);
            var vehicle = slash >= 0 ? text[(slash + 3)..].Trim() : string.Empty;
            return vehicle.Length > 0 ? vehicle : t("رسید گروهی", "Bulk receipt");
        }

        var match = SystemReferencePattern().Match(text);
        if (!match.Success)
        {
            return text;
        }

        var kind = match.Groups["kind"].Value.ToUpperInvariant() switch
        {
            "LOADING-RECEIPT" => t("رسید بارگیری", "Loading receipt"),
            "TRANSPORT-ALLOCATION" or "TRANSPORT-LEG" => t("حمل", "Transport"),
            "TRANSPORT-RECEIPT" => t("رسید حمل", "Transport receipt"),
            "VESSEL-DIRECT-LEG" => t("حمل از کشتی", "Vessel transport"),
            "LOSS" => t("ضایعات", "Loss"),
            "TRUCK-UNLOAD" => t("تخلیهٔ موتر", "Truck unload"),
            "TRUCK-DISPATCH" => t("ارسال موتر", "Truck dispatch"),
            _ => null
        };

        return kind is null ? text : $"{kind} #{match.Groups["id"].Value}";
    }

    [GeneratedRegex(@"^(?<kind>[A-Za-z]+(?:-[A-Za-z]+)*):(?<id>\d+)(?::.*)?$")]
    private static partial Regex SystemReferencePattern();
}
