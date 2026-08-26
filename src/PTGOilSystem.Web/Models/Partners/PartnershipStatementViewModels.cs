using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Models.Partners;

public sealed class PartnershipStatementPageViewModel
{
    public IReadOnlyList<PartnershipPairOption> Pairs { get; init; } = [];
    public PartnershipStatement? Statement { get; init; }
}

public sealed class PartnerSettlementFormViewModel
{
    [Display(Name = "تاریخ")]
    public DateTime SettlementDate { get; set; } = DateTime.Today;

    [Display(Name = "از شریک")]
    public int FromPartnerId { get; set; }

    [Display(Name = "به شریک")]
    public int ToPartnerId { get; set; }

    [Display(Name = "قرارداد")]
    public int? ContractId { get; set; }

    [Display(Name = "مبلغ")]
    public decimal Amount { get; set; }

    [Display(Name = "ارز")]
    [MaxLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به دالر")]
    public decimal AppliedFxRateToUsd { get; set; } = 1m;

    [Display(Name = "مرجع")]
    [MaxLength(200)]
    public string? Reference { get; set; }

    [Display(Name = "توضیح")]
    [MaxLength(1000)]
    public string? Description { get; set; }
}
