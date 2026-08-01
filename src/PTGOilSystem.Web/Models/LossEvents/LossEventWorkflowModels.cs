using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.LossEvents;

public sealed class StageLossCaptureInput
{
    public bool Enabled { get; set; }
    public LossEventStage Stage { get; set; } = LossEventStage.ReceiptShortage;
    public decimal? QuantityMt { get; set; }
    public decimal? ToleranceQuantityMt { get; set; }

    [StringLength(200)]
    public string? ResponsiblePartyName { get; set; }

    [StringLength(200)]
    public string? Reference { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class StageLossCaptureContext
{
    public LossEventStage Stage { get; init; }
    public bool AllowCustomStage { get; init; }
    public decimal ActualQuantityMt { get; init; }
    public DateTime EventDate { get; init; }
    public int ProductId { get; init; }
    public int? ContractId { get; init; }
    public int? ShipmentId { get; init; }
    public int? LoadingRegisterId { get; init; }
    public int? LoadingReceiptId { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? SalesTransactionId { get; init; }
    public int? TerminalId { get; init; }
    public int? StorageTankId { get; init; }
    public string? DefaultReference { get; init; }
    public string? DefaultNotes { get; init; }
}

public sealed class LossEventSubmission
{
    public LossEventStage Stage { get; set; }
    public int ProductId { get; set; }
    public int? ContractId { get; set; }
    public int? ShipmentId { get; set; }
    public int? LoadingRegisterId { get; set; }
    public int? LoadingReceiptId { get; set; }
    public int? TruckDispatchId { get; set; }
    public int? SalesTransactionId { get; set; }
    public int? TerminalId { get; set; }
    public int? StorageTankId { get; set; }
    public DateTime EventDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public decimal ExpectedQuantityMt { get; set; }
    public decimal ActualQuantityMt { get; set; }
    public decimal ToleranceQuantityMt { get; set; }
    public string? ResponsiblePartyType { get; set; }
    public string? ResponsiblePartyName { get; set; }
    public string? FinancialTreatment { get; set; }
    public bool AffectsInventory { get; set; }
    // سطح قطعیت ضایعه (اختیاری). فقط تسویهٔ نهایی مخزن آن را ست می‌کند.
    public LossCertaintyLevel? LossCertainty { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}

public sealed record LossEventComputation(
    decimal DifferenceQuantityMt,
    decimal AllowableLossMt,
    decimal ChargeableLossMt);

public sealed record LossEventWorkflowResult(
    LossEvent LossEvent,
    InventoryMovement? InventoryMovement,
    LossEventComputation Computation);

public static class StageLossCaptureMapper
{
    public static void Validate(
        StageLossCaptureInput? input,
        Action<string, string> addError,
        IReadOnlyCollection<LossEventStage>? allowedStages = null)
    {
        if (input is null || !input.Enabled)
        {
            return;
        }

        if (!input.QuantityMt.HasValue || input.QuantityMt.Value <= 0m)
        {
            addError(nameof(StageLossCaptureInput.QuantityMt), "مقدار ضایعات باید بزرگ‌تر از صفر باشد.");
        }

        if (input.ToleranceQuantityMt.HasValue && input.ToleranceQuantityMt.Value < 0m)
        {
            addError(nameof(StageLossCaptureInput.ToleranceQuantityMt), "تلورانس ضایعات نمی‌تواند منفی باشد.");
        }

        if (allowedStages is not null
            && allowedStages.Count > 0
            && !allowedStages.Contains(input.Stage))
        {
            addError(nameof(StageLossCaptureInput.Stage), "مرحله انتخاب‌شده برای ثبت ضایعات معتبر نیست.");
        }
    }

    public static LossEventSubmission ToSubmission(
        StageLossCaptureInput input,
        StageLossCaptureContext context)
    {
        var lossQuantityMt = input.QuantityMt.GetValueOrDefault();

        return new LossEventSubmission
        {
            Stage = context.AllowCustomStage ? input.Stage : context.Stage,
            ProductId = context.ProductId,
            ContractId = context.ContractId,
            ShipmentId = context.ShipmentId,
            LoadingRegisterId = context.LoadingRegisterId,
            LoadingReceiptId = context.LoadingReceiptId,
            TruckDispatchId = context.TruckDispatchId,
            SalesTransactionId = context.SalesTransactionId,
            TerminalId = context.TerminalId,
            StorageTankId = context.StorageTankId,
            EventDate = context.EventDate,
            ExpectedQuantityMt = context.ActualQuantityMt + lossQuantityMt,
            ActualQuantityMt = context.ActualQuantityMt,
            ToleranceQuantityMt = input.ToleranceQuantityMt.GetValueOrDefault(),
            ResponsiblePartyName = input.ResponsiblePartyName,
            AffectsInventory = false,
            Reference = string.IsNullOrWhiteSpace(input.Reference) ? context.DefaultReference : input.Reference,
            Notes = MergeText(input.Notes, context.DefaultNotes)
        };
    }

    private static string? MergeText(string? primary, string? secondary)
    {
        var normalizedPrimary = string.IsNullOrWhiteSpace(primary) ? null : primary.Trim();
        var normalizedSecondary = string.IsNullOrWhiteSpace(secondary) ? null : secondary.Trim();

        if (normalizedPrimary is null)
        {
            return normalizedSecondary;
        }

        if (normalizedSecondary is null)
        {
            return normalizedPrimary;
        }

        var combined = $"{normalizedPrimary} | {normalizedSecondary}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }
}
