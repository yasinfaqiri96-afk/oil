using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Services.Assistant;

/// <summary>
/// بررسی پیکربندی دستیار هنگام بالا آمدن برنامه.
///
/// عمداً هیچ‌گاه استثنا پرتاب نمی‌کند: نبودن کلید دستیار نباید کل سامانه را از کار
/// بیندازد. نتیجه فقط در Log می‌نشیند و خودِ دکمهٔ راهنما سر جایش می‌ماند؛ کاربر در
/// صورت اشکال، پیام مدیریت‌شدهٔ روشن می‌بیند. هیچ مقدار کلیدی Log نمی‌شود.
/// </summary>
public static class AssistantStartupValidator
{
    public static void Validate(IServiceProvider services, ILogger logger)
    {
        try
        {
            var options = services.GetRequiredService<IOptions<AssistantOptions>>().Value;
            if (!options.Enabled)
            {
                logger.LogInformation("Assistant is disabled by configuration.");
                return;
            }

            var providers = services.GetServices<IAssistantProvider>().ToList();
            Check(logger, providers, options.Provider, isFallback: false);

            if (!string.IsNullOrWhiteSpace(options.FallbackProvider))
            {
                Check(logger, providers, options.FallbackProvider, isFallback: true);
            }
        }
        catch (Exception ex)
        {
            // حتی خطای غیرمنتظره در همین بررسی نباید جلوی بالا آمدن برنامه را بگیرد.
            logger.LogError(ex, "Assistant configuration could not be validated at startup.");
        }
    }

    private static void Check(ILogger logger, List<IAssistantProvider> providers, string? name, bool isFallback)
    {
        var role = isFallback ? "fallback" : "primary";

        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            logger.LogError(
                "Assistant {Role} provider '{Provider}' is not registered. Known providers: {Known}.",
                role,
                name,
                string.Join(", ", providers.Select(p => p.Name)));
            return;
        }

        if (!provider.IsConfigured)
        {
            // فقط نام متغیر محیطی گزارش می‌شود، هرگز مقدار آن.
            logger.LogWarning(
                "Assistant {Role} provider '{Provider}' has no API key. Set the {Variable} environment variable. "
                + "The assistant button stays visible and shows a clear message until the key is set.",
                role,
                provider.Name,
                VariableFor(provider.Name));
            return;
        }

        logger.LogInformation("Assistant {Role} provider '{Provider}' is configured.", role, provider.Name);
    }

    private static string VariableFor(string providerName)
    {
        if (string.Equals(providerName, GeminiAssistantProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return "GEMINI_API_KEY";
        }

        return string.Equals(providerName, GroqAssistantProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            ? "GROQ_API_KEY"
            : "ANTHROPIC_API_KEY";
    }
}
