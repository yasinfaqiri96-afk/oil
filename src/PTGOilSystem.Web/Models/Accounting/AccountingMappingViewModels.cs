using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Accounting;

/// <summary>وضعیتِ یک نقشِ Mapping؛ هر مقدارِ غیر از None یعنی Adapterها سندِ آن نقش را Skip یا رد می‌کنند.</summary>
public enum AccountingMappingIssue
{
    None = 0,
    NotMapped = 1,
    Missing = 2,
    WrongCompany = 3,
    Inactive = 4,
    WrongType = 5,
    Duplicate = 6
}

public sealed record AccountingMappingOption(int Id, string Code, string Name);

public sealed record AccountingMappingRowViewModel(
    string Key,
    string Section,
    string SectionEn,
    string Label,
    string LabelEn,
    string Usage,
    string UsageEn,
    AccountType ExpectedType,
    int? AccountId,
    string? AccountCode,
    string? AccountName,
    AccountingMappingIssue Issue,
    bool IsLocked,
    IReadOnlyList<AccountingMappingOption> Options);

public sealed record AccountingMappingPageViewModel(
    int? OwnerCompanyId,
    bool SettingsExist,
    string? FunctionalCurrencyCode,
    IReadOnlyList<AccountingMappingRowViewModel> Rows);
