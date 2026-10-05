using Microsoft.AspNetCore.Mvc.Rendering;

namespace PTGOilSystem.Web.Models.Sales;

// خریدارِ فروش در فرم یک فیلد است: «C:شناسه» برای مشتری و «S:شناسه» برای تأمین‌کننده.
// فروش به تأمین‌کننده سطر لجرِ فروش را با SupplierId می‌نویسد و از طلبِ او کم می‌کند.
public static class SaleBuyerKey
{
    private const string CustomerPrefix = "C:";
    private const string SupplierPrefix = "S:";

    public static string? Build(int? customerId, int? supplierId)
        => supplierId is > 0 ? SupplierPrefix + supplierId.Value
            : customerId is > 0 ? CustomerPrefix + customerId.Value
            : null;

    public static (int? CustomerId, int? SupplierId) Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (null, null);

        key = key.Trim();
        if (key.StartsWith(SupplierPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[SupplierPrefix.Length..], out var supplierId) && supplierId > 0)
            return (null, supplierId);

        if (key.StartsWith(CustomerPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[CustomerPrefix.Length..], out var prefixedCustomerId) && prefixedCustomerId > 0)
            return (prefixedCustomerId, null);

        // سازگاری با لینک/فرم قدیمی که فقط شناسهٔ مشتری می‌فرستاد.
        return int.TryParse(key, out var customerId) && customerId > 0 ? (customerId, null) : (null, null);
    }

    public static List<SelectListItem> BuildOptions(
        IEnumerable<(int Id, string Name)> customers,
        IEnumerable<(int Id, string Name)> suppliers,
        string? selectedKey)
    {
        var customerGroup = new SelectListGroup { Name = "مشتریان" };
        var supplierGroup = new SelectListGroup { Name = "تأمین‌کنندگان" };
        var items = new List<SelectListItem>();
        foreach (var (id, name) in customers)
        {
            var value = CustomerPrefix + id;
            items.Add(new SelectListItem(name, value, value == selectedKey) { Group = customerGroup });
        }

        foreach (var (id, name) in suppliers)
        {
            var value = SupplierPrefix + id;
            items.Add(new SelectListItem(name, value, value == selectedKey) { Group = supplierGroup });
        }

        return items;
    }
}
