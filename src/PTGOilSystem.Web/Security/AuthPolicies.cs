namespace PTGOilSystem.Web.Security;

public static class AuthPolicies
{
    public const string ManageData = nameof(ManageData);
    public const string AdminOnly = nameof(AdminOnly);

    /// <summary>
    /// صفحهٔ پشتیبان‌گیری. بالاترین سطح دسترسی سیستم (نقش Admin = SuperAdmin) یا
    /// Permission صریح ManageBackups. مدیریت کاربران به‌تنهایی کافی نیست.
    /// </summary>
    public const string BackupAdmin = nameof(BackupAdmin);
}
