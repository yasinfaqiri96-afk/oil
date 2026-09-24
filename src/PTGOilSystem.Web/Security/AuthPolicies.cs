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

    /// <summary>
    /// Mashal Mobile — فقط JWT Bearer. کوکی مرورگر روی <c>/api/mobile</c> پذیرفته نمی‌شود؛ نقش و
    /// ناوبری همان کاربر و همان <see cref="RoleAccessRules"/> وب است.
    /// </summary>
    public const string MobileApi = nameof(MobileApi);

    // مدیریت بشری — نقش Admin یا Permission صریح در نقش.
    public const string HrViewSalary = nameof(HrViewSalary);
    public const string HrManageSalary = nameof(HrManageSalary);
    public const string HrRunPayroll = nameof(HrRunPayroll);
    public const string HrPaySalary = nameof(HrPaySalary);
}
