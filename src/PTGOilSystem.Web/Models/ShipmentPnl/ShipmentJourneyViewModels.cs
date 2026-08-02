using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.ShipmentPnl;

// «ضایعات محموله» — مدل نمایشی ردیف ضایعات که در صفحهٔ مادر
// (ShipmentPnl/Details، تب «مصارف و کسورات») استفاده می‌شود.
// مدل‌های legacy نمای کشتی (ShipmentJourneyViewModel و ShipmentJourneySourceContractItem)
// پس از ادغام کامل در Details و حذف Views/ShipmentPnl/Journey.cshtml برداشته شدند.
public sealed class ShipmentJourneyLossItem
{
    public int Id { get; init; }
    public DateTime EventDate { get; init; }
    public string StageName { get; init; } = string.Empty;
    public decimal DifferenceQuantityMt { get; init; }
    public decimal ChargeableLossMt { get; init; }
    public int? ContractId { get; init; }
    public string? ContractName { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractDisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public string? ResponsiblePartyType { get; init; }
    public string? ResponsiblePartyName { get; init; }
    public string? FinancialTreatment { get; init; }
    public string? Reference { get; init; }
    public string? AllocationGroupKey { get; init; }
    public string? Notes { get; init; }
    /// <summary>نمبر وسیلهٔ مرحله‌ای که کسری روی آن ثبت شده.</summary>
    public string? VehicleNumber { get; init; }
    public string ResponsibilityTypeName
        => ResponsiblePartyType switch
        {
            ShipmentShortageResponsibilityTypes.CompanyLoss => "ضرر شرکت",
            ShipmentShortageResponsibilityTypes.SupplierDeduction => "کسر از حساب تأمین‌کننده",
            ShipmentShortageResponsibilityTypes.ServiceProviderClaim => "طلب از شرکت خدماتی",
            ShipmentShortageResponsibilityTypes.PartnerShareDeduction => "کسر از سهم شریک",
            ShipmentShortageResponsibilityTypes.Split => "تقسیم بین چند طرف",
            _ => string.IsNullOrWhiteSpace(ResponsiblePartyName) ? "نامشخص" : ResponsiblePartyName
        };
}
