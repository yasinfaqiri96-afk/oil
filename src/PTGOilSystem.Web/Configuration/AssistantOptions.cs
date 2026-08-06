namespace PTGOilSystem.Web.Configuration;

/// <summary>
/// تنظیمات «راهنمای هوشمند». از بخش "Assistant" در پیکربندی خوانده می‌شود.
/// هیچ کلید API اینجا نگهداری نمی‌شود؛ هر Provider کلیدش را فقط از متغیر محیطی
/// خودش می‌خواند (GEMINI_API_KEY / GROQ_API_KEY / ANTHROPIC_API_KEY).
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>روشن/خاموش بودن قابلیت. خاموش باشد، دکمه راهنما رندر نمی‌شود.</summary>
    public bool Enabled { get; set; }

    /// <summary>سرویس مورد استفاده: "Gemini"، "Groq" یا "Anthropic". بدون تغییر کد قابل تعویض است.</summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>نام مدل Claude (وقتی Provider = Anthropic). بدون تغییر کد قابل تعویض است.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>تنظیمات Gemini.</summary>
    public AssistantGeminiOptions Gemini { get; set; } = new();

    /// <summary>تنظیمات Groq (API سازگار با OpenAI).</summary>
    public AssistantGroqOptions Groq { get; set; } = new();

    /// <summary>
    /// Provider جایگزین وقتی Provider اصلی خطای *موقت* می‌دهد. خالی باشد، جایگزینی
    /// انجام نمی‌شود. عمداً فقط برای خطای گذرا به کار می‌رود: خطای مجوز، کلید
    /// نامعتبر یا منطقهٔ پشتیبانی‌نشده باید دیده شود، نه پنهان.
    /// </summary>
    public string? FallbackProvider { get; set; }

    /// <summary>نام مدلی که Provider جایگزین با آن صدا زده می‌شود. خالی باشد، مدل پیش‌فرض همان Provider.</summary>
    public string? FallbackModel { get; set; }

    /// <summary>سقف توکن پاسخ. برای کوتاه ماندن پاسخ و کنترل هزینه.</summary>
    public int MaxOutputTokens { get; set; } = 2000;

    /// <summary>
    /// اجازهٔ خواندن داده واقعی. خاموش باشد، دستیار فقط راهنمایی می‌کند و هیچ Tool
    /// اجرا نمی‌شود. روشن باشد، فقط Toolهای فقط‌خواندنیِ ثبت‌شده در دسترس‌اند و هر
    /// کدام دسترسی همان کاربر را دوباره بررسی می‌کنند.
    /// </summary>
    public bool EnableTools { get; set; } = true;

    /// <summary>
    /// حداکثر دور فراخوانی Tool در یک سؤال. حلقه را کران‌دار نگه می‌دارد تا مدل
    /// نتواند بی‌پایان Tool صدا بزند.
    /// </summary>
    public int MaxToolIterations { get; set; } = 4;

    /// <summary>حداکثر تعداد ردیف بازگشتی از هر Tool. جلوی پاسخ‌های غول‌پیکر را می‌گیرد.</summary>
    public int MaxToolRows { get; set; } = 25;

    /// <summary>
    /// تعداد پیام‌های قبلی گفتگو که همراه سؤال فرستاده می‌شود (هر نوبت = سؤال + پاسخ).
    /// برای پشتیبانی از سؤال دنباله‌دار مثل «و بعدش؟».
    /// </summary>
    public int MaxHistoryTurns { get; set; } = 6;

    /// <summary>حداکثر طول سؤال کاربر (کاراکتر).</summary>
    public int MaxQuestionLength { get; set; } = 500;

    /// <summary>حداکثر تعداد فیلدهای صفحه که به Backend فرستاده می‌شود.</summary>
    public int MaxContextFields { get; set; } = 40;

    /// <summary>حداکثر تعداد دکمه‌های صفحه که به Backend فرستاده می‌شود.</summary>
    public int MaxContextButtons { get; set; } = 15;

    /// <summary>حداکثر طول پیام خطای ارسالی (کاراکتر).</summary>
    public int MaxErrorLength { get; set; } = 300;

    /// <summary>مهلت درخواست به سرویس هوش مصنوعی (ثانیه).</summary>
    public int TimeoutSeconds { get; set; } = 45;
}

/// <summary>
/// تنظیمات Gemini. کلید هرگز اینجا نگهداری نمی‌شود؛ فقط از متغیر محیطی GEMINI_API_KEY خوانده می‌شود.
/// </summary>
public sealed class AssistantGeminiOptions
{
    /// <summary>
    /// نام مدل Gemini. بدون تغییر کد قابل تعویض است.
    /// عمداً هیچ جایگزینی خودکار برای این مدل انجام نمی‌شود: اگر مدل برای کلید یا
    /// منطقهٔ فعلی در دسترس نباشد، خطای واقعی گزارش می‌شود تا تصمیم با آدمی باشد.
    /// </summary>
    public string Model { get; set; } = "gemini-3.6-flash";
}

/// <summary>
/// تنظیمات Groq. کلید هرگز اینجا نگهداری نمی‌شود؛ فقط از متغیر محیطی GROQ_API_KEY خوانده می‌شود.
/// </summary>
public sealed class AssistantGroqOptions
{
    /// <summary>آدرس API سازگار با OpenAI در Groq.</summary>
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";

    /// <summary>
    /// نام مدل Groq. بدون تغییر کد قابل تعویض است.
    /// llama-3.3-70b-versatile انتخاب شده چون دری/فارسی را خوب می‌نویسد، Tool Calling
    /// را پشتیبانی می‌کند و برخلاف مدل‌های reasoning بودجهٔ توکن پاسخ را صرف تفکر داخلی نمی‌کند.
    /// </summary>
    public string Model { get; set; } = "llama-3.3-70b-versatile";
}
