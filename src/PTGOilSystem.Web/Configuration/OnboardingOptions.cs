namespace PTGOilSystem.Web.Configuration;

/// <summary>
/// راهنمای شروع کار (چک‌لیست راه‌اندازی، «می‌خواهید چه کاری انجام دهید؟» و تورهای کوتاه).
/// از بخش "Onboarding" در پیکربندی خوانده می‌شود. پیش‌فرض خاموش است تا نصب‌های موجود
/// دقیقاً مثل قبل بمانند؛ فقط نسخه‌های آزمایشی (Trial) آن را روشن می‌کنند.
/// </summary>
public sealed class OnboardingOptions
{
    public const string SectionName = "Onboarding";

    /// <summary>خاموش باشد، هیچ کارت، دکمه، CSS یا JS راهنما رندر نمی‌شود.</summary>
    public bool Enabled { get; set; }
}
