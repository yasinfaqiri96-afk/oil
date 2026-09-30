using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Models;

/// <summary>ورودی کارت «راهنمای شروع» داشبورد. فقط وقتی ساخته می‌شود که راهنما روشن باشد.</summary>
public sealed class OnboardingPanelViewModel
{
    public required OnboardingProgress Progress { get; init; }

    /// <summary>کاربر از دکمهٔ «راهنما» آمده؛ کارت حتی اگر قبلاً بسته شده باشد باز می‌شود.</summary>
    public bool ForceOpen { get; init; }

    /// <summary>کلید ذخیرهٔ وضعیت بستن/دیدن در مرورگر (نام کوکی و localStorage)؛ برای هر کاربر جدا.</summary>
    public required string StorageKey { get; init; }

    /// <summary>کارت‌هایی که کاربر بسته است («setup» یا «tasks»)، از کوکی همان کلید.</summary>
    public string Dismissed { get; init; } = "";

    public bool IsDismissed(string panel)
        => !ForceOpen && Dismissed.Split('.').Contains(panel, StringComparer.Ordinal);

    /// <summary>نام کوکی فقط حروف لاتین، عدد، خط تیره و زیرخط می‌پذیرد.</summary>
    public static string BuildStorageKey(string? userName)
    {
        var safe = new string((userName ?? "user")
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());
        return "ptg-onb-" + (safe.Length == 0 ? "user" : safe);
    }
}
