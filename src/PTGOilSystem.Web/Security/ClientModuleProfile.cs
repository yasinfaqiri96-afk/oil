using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;

namespace PTGOilSystem.Web.Security;

/// <summary>
/// لایهٔ Client Profile روی دسترسی: ماژولی که برای این Instance مخفی است برای هیچ نقشی
/// (حتی Admin) در منو دیده نمی‌شود و URL مستقیمش هم بسته است. ماژول فعال همچنان باید از
/// Role/Permission عبور کند؛ Profile جای آن را نمی‌گیرد. بدون تنظیم، همه‌چیز فعال است.
/// </summary>
public sealed partial class ClientModuleProfile
{
    // این Controllerها زیرساخت ورود/خطا/داشبوردند و با Profile بسته نمی‌شوند.
    private static readonly HashSet<string> AlwaysEnabledControllers =
        new(["Home", "Auth", "Assistant", "Error"], StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _hiddenModules;
    private readonly HashSet<string> _hiddenControllers;

    public static ClientModuleProfile Full { get; } = new(new ClientProfileOptions());

    public ClientModuleProfile(IOptions<ClientProfileOptions> options)
        : this(options.Value)
    {
    }

    public ClientModuleProfile(ClientProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Name = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
        SimplePurchaseEnabled = options.SimplePurchaseEnabled;
        _hiddenModules = Normalize(options.HiddenModules);
        _hiddenModules.Remove(RoleNavigationKeys.Dashboard);
        _hiddenControllers = Normalize(options.HiddenControllers);
        _hiddenControllers.ExceptWith(AlwaysEnabledControllers);
    }

    public string? Name { get; }

    public bool SimplePurchaseEnabled { get; }

    public bool HasRestrictions => _hiddenModules.Count > 0 || _hiddenControllers.Count > 0;

    public bool IsNavigationEnabled(string? navigationKey)
        => string.IsNullOrWhiteSpace(navigationKey) || !_hiddenModules.Contains(navigationKey.Trim());

    public bool IsControllerEnabled(string? controller)
    {
        if (string.IsNullOrWhiteSpace(controller) || !HasRestrictions)
        {
            return true;
        }

        if (_hiddenControllers.Contains(controller))
        {
            return false;
        }

        return IsNavigationEnabled(RoleAccessRules.NavigationKeyForController(controller));
    }

    /// <summary>نام Profile فقط حروف/عدد/خط تیره باشد تا نام فایل config قابل سوءاستفاده نباشد.</summary>
    public static bool IsValidProfileName(string? name)
        => !string.IsNullOrWhiteSpace(name) && ProfileNamePattern().IsMatch(name.Trim());

    private static HashSet<string> Normalize(IEnumerable<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ProfileNamePattern();
}
