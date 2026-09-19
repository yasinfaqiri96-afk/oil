namespace PTGOilSystem.Web.Security;

/// <summary>
/// کلید ناوبریِ یک API. برای مسیرهای <c>/api</c>، <see cref="RoleNavigationAuthorizationFilter"/>
/// به‌جای نام کنترلر همین کلید را با همان <see cref="RoleAccessRules"/> وب می‌سنجد و به‌جای
/// Redirect به صفحهٔ HTML، پاسخ 403 JSON می‌دهد. API بدون این ویژگی رد می‌شود (fail closed).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ApiNavigationAttribute(string navigationKey) : Attribute
{
    public string NavigationKey { get; } = navigationKey;
}
