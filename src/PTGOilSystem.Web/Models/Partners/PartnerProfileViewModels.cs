using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Models.Partners;

/// <summary>
/// پروفایل شریک — فقط هویت شریک به‌علاوهٔ صورت‌حسابی که سرویس شراکت ساخته است.
///
/// هیچ عددی اینجا محاسبه نمی‌شود: تمام ارقام مالی از
/// <see cref="IPartnershipStatementService.BuildForPartnerAsync"/> می‌آیند تا پروفایل و
/// صورت‌حساب شراکت دقیقاً یک عدد نشان دهند.
/// </summary>
public sealed class PartnerProfileViewModel
{
    public int PartnerId { get; init; }
    public int Id => PartnerId;
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NamePersian { get; init; }
    public string? Country { get; init; }
    public string? ContactPerson { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Email { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>«summary» = خلاصه حساب، «ledger» = گردش حساب. تب سومی وجود ندارد.</summary>
    public string ActiveTab { get; init; } = PartnerProfileTabs.Summary;

    /// <summary>صورت‌حساب شریک روی همهٔ قراردادها — منبع یگانهٔ ارقام صفحه.</summary>
    public PartnerAccountStatement? Statement { get; init; }

    // فیلترهای تب گردش حساب. مانده تجمعی همیشه از ابتدای حساب است و با فیلتر تغییر نمی‌کند؛
    // فیلتر فقط تعیین می‌کند کدام ردیف‌ها دیده شوند.
    public int? FilterContractId { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }

    /// <summary>ردیف‌های گردش حساب پس از اعمال فیلترهای نمایشی.</summary>
    public IReadOnlyList<PartnerAccountEntry> Entries { get; init; } = [];

    public IReadOnlyList<PartnerContractPosition> Contracts => Statement?.Contracts ?? [];
    public IReadOnlyList<PartnershipContractOption> ContractOptions => Statement?.ContractOptions ?? [];
    public IReadOnlyList<PartnerCoPartner> CoPartners => Statement?.CoPartners ?? [];
    public int ContractsCount => Contracts.Count;
}

public static class PartnerProfileTabs
{
    public const string Summary = "summary";
    public const string Ledger = "ledger";

    public static string Resolve(string? tab)
        => string.Equals((tab ?? string.Empty).Trim(), Ledger, StringComparison.OrdinalIgnoreCase)
            ? Ledger
            : Summary;
}
