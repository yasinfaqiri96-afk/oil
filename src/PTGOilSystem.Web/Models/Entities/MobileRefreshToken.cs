using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// Mashal Mobile — یک حلقه از توکن تازه‌سازیِ یک نشست موبایل.
///
/// خودِ توکن هرگز ذخیره نمی‌شود؛ فقط هش SHA-256 آن. هر استفادهٔ موفق این ردیف را باطل و
/// ردیف تازه‌ای در همان <see cref="SessionId"/> می‌سازد (rotation). استفادهٔ دوباره از ردیفِ
/// چرخیده یعنی توکن تکرار/دزدی شده و کل همان نشست باطل می‌شود. نشستِ هر گوشی جدا از
/// گوشی‌های دیگرِ همان کاربر باطل‌شدنی است.
/// </summary>
public class MobileRefreshToken : BaseEntity
{
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 هگزِ کوچکِ توکن خام (۶۴ کاراکتر).</summary>
    [Required, MaxLength(64)] public string TokenHash { get; set; } = "";

    /// <summary>نشستِ یک ورود؛ همهٔ توکن‌های چرخیدهٔ همان ورود یک SessionId دارند.</summary>
    public Guid SessionId { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    [MaxLength(40)] public string? RevokedReason { get; set; }

    public int? ReplacedByTokenId { get; set; }
    public MobileRefreshToken? ReplacedByToken { get; set; }

    [MaxLength(100)] public string? DeviceId { get; set; }
    [MaxLength(100)] public string? DeviceName { get; set; }
    [MaxLength(64)] public string? CreatedByIp { get; set; }
    [MaxLength(64)] public string? RevokedByIp { get; set; }
}

public static class MobileRefreshTokenRevocationReasons
{
    public const string Rotated = "Rotated";
    public const string Logout = "Logout";
    public const string ReuseDetected = "ReuseDetected";
    public const string UserInactive = "UserInactive";
    public const string PasswordChanged = "PasswordChanged";
}
