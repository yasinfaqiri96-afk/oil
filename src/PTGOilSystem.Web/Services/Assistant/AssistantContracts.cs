namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>
/// Context صفحه‌ای که Frontend همراه سؤال می‌فرستد. فقط ساختار صفحه است؛
/// هیچ مقدار مالی، مبلغ یا داده رکورد در آن قرار نمی‌گیرد.
/// </summary>
public sealed class AssistantPageContext
{
    public string? Route { get; set; }
    public string? Controller { get; set; }
    public string? Action { get; set; }
    public string? PageTitle { get; set; }
    public List<string> Fields { get; set; } = new();
    public List<string> Buttons { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public string? FocusedField { get; set; }
}

/// <summary>
/// یک نوبت قبلی گفتگو. برای سؤال دنباله‌دار مثل «و بعدش؟» لازم است.
/// تاریخچه از Frontend می‌آید و در Backend محدود و پاک‌سازی می‌شود؛
/// هیچ نقشی از آن استنتاج نمی‌شود و هیچ دسترسی‌ای با آن باز نمی‌گردد.
/// </summary>
public sealed class AssistantTurn
{
    /// <summary>"user" یا "assistant". هر مقدار دیگری نادیده گرفته می‌شود.</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

/// <summary>ورودی درخواست از پنل چت.</summary>
public sealed class AssistantAskRequest
{
    public string Question { get; set; } = string.Empty;
    public AssistantPageContext Context { get; set; } = new();

    /// <summary>نوبت‌های قبلی همین گفتگو، قدیمی به جدید.</summary>
    public List<AssistantTurn> History { get; set; } = new();
}

/// <summary>نتیجهٔ پاسخ‌دهی؛ خطا هم به‌صورت قابل‌فهم برمی‌گردد.</summary>
public sealed record AssistantAnswer(bool Ok, string Message)
{
    /// <summary>
    /// نام Toolهایی که برای ساختن این پاسخ اجرا شدند. برای شفافیت به کاربر
    /// نشان داده می‌شود تا معلوم باشد پاسخ از داده واقعی آمده یا از راهنما.
    /// </summary>
    public IReadOnlyList<string> UsedTools { get; init; } = Array.Empty<string>();
}
