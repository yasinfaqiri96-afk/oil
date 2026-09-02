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

    /// <summary>
    /// PTG-P1-01 — بستن و بازکردنِ دورهٔ عملیاتی. مثل صفحهٔ پشتیبان‌گیری: نقش Admin یا
    /// Permission صریح ManageOperationalPeriodLock.
    /// </summary>
    public const string OperationalPeriodAdmin = nameof(OperationalPeriodAdmin);
}
