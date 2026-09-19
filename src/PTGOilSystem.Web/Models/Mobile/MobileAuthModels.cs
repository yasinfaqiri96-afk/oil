using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Mobile;

public sealed class MobileLoginRequest
{
    [Required(ErrorMessage = "نام کاربری اجباری است.")]
    [MaxLength(100)]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "رمز عبور اجباری است.")]
    [MaxLength(256)]
    public string Password { get; set; } = "";

    [MaxLength(100)]
    public string? DeviceId { get; set; }

    [MaxLength(100)]
    public string? DeviceName { get; set; }
}

public sealed class MobileRefreshRequest
{
    [Required(ErrorMessage = "توکن تازه‌سازی اجباری است.")]
    [MaxLength(200)]
    public string RefreshToken { get; set; } = "";

    [MaxLength(100)]
    public string? DeviceId { get; set; }
}

public sealed class MobileLogoutRequest
{
    [MaxLength(200)]
    public string? RefreshToken { get; set; }
}

public sealed class MobileAuthResponse
{
    public string TokenType { get; init; } = "Bearer";
    public string AccessToken { get; init; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; init; }
    public string RefreshToken { get; init; } = "";
    public DateTime RefreshTokenExpiresAtUtc { get; init; }
    public MobileMeResponse User { get; init; } = new();
}

/// <summary>پروفایل امن کاربر برای موبایل؛ هیچ هش، Security stamp یا فیلد داخلی ندارد.</summary>
public sealed class MobileMeResponse
{
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "";
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>کلیدهای ناوبریِ مجاز — همان مقادیر <c>RoleNavigationKeys</c> وب.</summary>
    public IReadOnlyList<string> Navigation { get; init; } = [];

    public MobileCapabilities Capabilities { get; init; } = new();
    public MobileCompanyInfo? Company { get; init; }
    public string Language { get; init; } = "fa-AF";
}

/// <summary>فقط برای نمایش/مخفی‌کردن UI. سرور هر endpoint را مستقل کنترل می‌کند.</summary>
public sealed class MobileCapabilities
{
    public bool Dashboard { get; init; }
    public bool Operations { get; init; }
    public bool Inventory { get; init; }
    public bool Sales { get; init; }
    public bool Finance { get; init; }
    public bool Reports { get; init; }
    public bool ManageData { get; init; }
}

public sealed class MobileCompanyInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? NamePersian { get; init; }
}
