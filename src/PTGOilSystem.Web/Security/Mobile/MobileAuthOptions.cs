using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PTGOilSystem.Web.Security.Mobile;

/// <summary>پیکربندی احراز هویت موبایل (بخش <c>MobileAuth</c>).</summary>
public sealed class MobileAuthOptions
{
    public const string SectionName = "MobileAuth";

    /// <summary>جایگزینِ <c>MobileAuth__SigningKey</c>؛ کلید هرگز در appsettings نوشته نمی‌شود.</summary>
    public const string SigningKeyEnvironmentVariable = "PTG_MOBILE_JWT_SIGNING_KEY";

    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = "PTGOilSystem";
    public string Audience { get; set; } = "mashal-mobile";
    public string? SigningKey { get; set; }
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>عمر مطلق نشست؛ چرخش توکن آن را تمدید نمی‌کند.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>ادعاهای داخل JWT موبایل — فقط شناسه‌ها، بدون نقش یا Permission.</summary>
public static class MobileClaimTypes
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string TokenId = "jti";
}

public enum MobileSigningKeyStatus
{
    Configured,
    DevelopmentEphemeral,
    Missing
}

/// <summary>
/// کلید امضای JWT. فقط از پیکربندی یا متغیر محیطی خوانده و هرگز لاگ نمی‌شود.
/// بدون کلید معتبر (حداقل ۳۲ بایت) در Production، وب بدون تغییر بالا می‌آید و فقط صدور توکن
/// موبایل غیرفعال است (503). در Development یک کلید تصادفیِ موقت ساخته می‌شود.
/// </summary>
public sealed class MobileJwtKeyProvider
{
    private MobileJwtKeyProvider(SymmetricSecurityKey key, MobileSigningKeyStatus status)
    {
        Key = key;
        Status = status;
    }

    public SymmetricSecurityKey Key { get; }

    public MobileSigningKeyStatus Status { get; }

    public bool CanIssueTokens => Status != MobileSigningKeyStatus.Missing;

    public static MobileJwtKeyProvider Create(string? configuredKey, bool isDevelopment)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey)
            && Encoding.UTF8.GetByteCount(configuredKey) >= MobileAuthOptions.MinimumSigningKeyBytes)
        {
            return new MobileJwtKeyProvider(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredKey)),
                MobileSigningKeyStatus.Configured);
        }

        // کلید تصادفی تا اعتبارسنجیِ Bearer همیشه پیکربندی شده باشد؛ در Production با آن توکنی صادر نمی‌شود.
        return new MobileJwtKeyProvider(
            new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)),
            isDevelopment ? MobileSigningKeyStatus.DevelopmentEphemeral : MobileSigningKeyStatus.Missing);
    }
}
