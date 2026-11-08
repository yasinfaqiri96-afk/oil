using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models;

namespace PTGOilSystem.Web.Services;

/// <summary>قرارداد مادرِ یک ردیف خرید چندتأمین‌کننده، فقط برای انتخاب و نمایش.</summary>
public sealed record PurchaseGroupRef(int Id, string GroupNumber, string Name)
{
    /// <summary>
    /// پیشوند مقدار فیلترِ قرارداد مادر؛ با شمارهٔ قرارداد عادی هم‌نام نمی‌شود.
    /// </summary>
    public const string FilterValuePrefix = "group:";

    public string FilterValue => FilterValuePrefix + GroupNumber;

    public string Label => string.IsNullOrWhiteSpace(Name) || string.Equals(Name, GroupNumber, StringComparison.Ordinal)
        ? GroupNumber
        : $"{Name} — {GroupNumber}";
}

/// <summary>
/// پیوند ردیف‌های قرارداد خرید چندتأمین‌کننده به قرارداد مادر برای انتخاب‌گرها.
/// انتخاب مادر فقط مجموعهٔ منابع/عملیاتِ همهٔ ردیف‌ها را یک‌جا در دسترس می‌گذارد؛ هر منبع
/// همچنان با ContractId واقعی همان ردیف ثبت می‌شود و مالکیت یا منشأ نفت تغییر نمی‌کند.
/// </summary>
public static class PurchaseContractGroupLookup
{
    public static async Task<IReadOnlyDictionary<string, PurchaseGroupRef>> LoadByContractNumberAsync(
        ApplicationDbContext db,
        IEnumerable<string?> contractNumbers,
        CancellationToken cancellationToken = default)
    {
        var numbers = contractNumbers
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (numbers.Count == 0)
        {
            return new Dictionary<string, PurchaseGroupRef>(StringComparer.Ordinal);
        }

        var rows = await db.Contracts
            .AsNoTracking()
            .Where(c => c.PurchaseContractGroupId != null && numbers.Contains(c.ContractNumber))
            .Select(c => new
            {
                c.ContractNumber,
                GroupId = c.PurchaseContractGroup!.Id,
                c.PurchaseContractGroup.GroupNumber,
                c.PurchaseContractGroup.Name
            })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, PurchaseGroupRef>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.ContractNumber] = new PurchaseGroupRef(row.GroupId, row.GroupNumber, row.Name);
        }

        return result;
    }

    /// <summary>
    /// گزینه‌های فیلتر قرارداد: اول قراردادهای مادر (هرکدام همهٔ ردیف‌هایش را می‌گیرد)، سپس
    /// قراردادهای تکی و ردیف‌ها برای انتخاب جداگانه در صورت نیاز.
    /// </summary>
    public static IReadOnlyList<AkFilterOption> BuildContractFilterOptions(
        IEnumerable<(string ContractNumber, PurchaseGroupRef? Group)> items)
    {
        var list = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ContractNumber))
            .ToList();

        var groupOptions = list
            .Where(item => item.Group is not null)
            .GroupBy(item => item.Group!.Id)
            .Select(group =>
            {
                var head = group.First().Group!;
                var lineCount = group.Select(item => item.ContractNumber).Distinct(StringComparer.Ordinal).Count();
                return new AkFilterOption(head.FilterValue, $"{head.Label} (قرارداد مادر، {lineCount} ردیف)");
            })
            .OrderBy(option => option.Label, StringComparer.Ordinal);

        var contractOptions = list
            .GroupBy(item => item.ContractNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupRef = group.First().Group;
                return new AkFilterOption(
                    group.Key,
                    groupRef is null ? group.Key : $"{group.Key} · {groupRef.GroupNumber}");
            })
            .OrderBy(option => option.Value, StringComparer.Ordinal);

        return groupOptions.Concat(contractOptions).ToList();
    }
}
