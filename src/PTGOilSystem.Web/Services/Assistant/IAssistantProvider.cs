namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>نوع شکست یک Provider تا پیام کاربر بدون افشای جزئیات فنی ساخته شود.</summary>
public enum AssistantFailure
{
    None = 0,

    /// <summary>کلید API تنظیم نشده است.</summary>
    NotConfigured,

    /// <summary>سقف نرخ یا ظرفیت رایگان تمام شده است (HTTP 429). موقت.</summary>
    RateLimited,

    /// <summary>پاسخ‌گویی بیش از حد طول کشید. موقت.</summary>
    Timeout,

    /// <summary>
    /// خطای سمت سرویس (HTTP 5xx). موقت؛ تلاش با Provider جایگزین مجاز است.
    /// </summary>
    ServiceError,

    /// <summary>
    /// خطای *دائمی* مجوز: کلید نامعتبر، دسترسی رد شده (HTTP 401/403)، مدل در
    /// دسترس این کلید نیست، یا منطقه پشتیبانی نمی‌شود.
    /// عمداً هرگز با Provider جایگزین پنهان نمی‌شود؛ باید دیده و اصلاح شود.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// خطای *دائمی*: سرویس از منطقهٔ این سرور در دسترس نیست. سرویس پیش از بررسی
    /// کلید و مدل، درخواست را رد می‌کند. با تلاش دوباره یا Provider جایگزین حل
    /// نمی‌شود؛ باید Provider عوض شود یا برنامه از منطقهٔ پشتیبانی‌شده اجرا شود.
    /// </summary>
    RegionUnsupported,

    /// <summary>
    /// قطع شبکه، DNS یا اتصال ناموفق. *گذرا* است: سرویس اصلاً پاسخ نداده، پس
    /// تلاش با Provider جایگزین درست است.
    /// </summary>
    NetworkError,

    /// <summary>
    /// خودِ درخواست ایراد دارد: Schema، ورودی، اندازه یا شکل پیام‌ها (HTTP 400 که
    /// نه مجوز است نه منطقه). *دائمی* است — همان درخواست خراب به Provider دوم هم
    /// می‌رود و همان‌جا هم رد می‌شود، پس جایگزینی فقط اشکال را پنهان می‌کند.
    /// </summary>
    InvalidRequest,

    /// <summary>هر خطای دیگر در تماس با سرویس.</summary>
    Unavailable,

    /// <summary>
    /// سرویس، فراخوانی ابزارِ ساخته‌شده توسط مدل را نپذیرفت (ورودی با Schema نخواند).
    /// قابل‌جبران است: همان سؤال بدون ابزار دوباره پرسیده می‌شود تا کاربر دست‌کم
    /// پاسخ راهنمایی بگیرد، نه پیام قطع ارتباط.
    /// </summary>
    ToolCallRejected,
}

/// <summary>
/// یک سرویس هوش مصنوعی برای «راهنمای هوشمند». Provider فقط گفتگو را به سرویس
/// می‌برد و پاسخ را برمی‌گرداند؛ هیچ منطق تجاری و هیچ دسترسی داده‌ای ندارد.
/// اجرای Tool و بررسی دسترسی همیشه در AssistantService انجام می‌شود.
/// </summary>
public interface IAssistantProvider
{
    /// <summary>نامی که در Assistant.Provider نوشته می‌شود (بدون حساسیت به بزرگی/کوچکی حروف).</summary>
    string Name { get; }

    /// <summary>آیا کلید API این Provider در متغیر محیطی موجود است.</summary>
    bool IsConfigured { get; }

    /// <summary>آیا این Provider فراخوانی Tool را پشتیبانی می‌کند.</summary>
    bool SupportsTools { get; }

    /// <summary>
    /// یک دور گفتگو. اگر <paramref name="tools"/> خالی نباشد، مدل می‌تواند به‌جای متن،
    /// درخواست اجرای Tool برگرداند؛ تصمیم اجرا با فراخوان است، نه با Provider.
    ///
    /// <paramref name="modelOverride"/> برای حالت جایگزینی است تا بتوان همان Provider
    /// را با مدل دیگری صدا زد. خالی باشد، مدل پیش‌فرض پیکربندی به کار می‌رود.
    /// </summary>
    Task<AssistantProviderResult> CompleteAsync(
        IReadOnlyList<AssistantChatMessage> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        int maxOutputTokens,
        string? modelOverride,
        CancellationToken cancellationToken);
}
