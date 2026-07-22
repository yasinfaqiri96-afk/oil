using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public sealed class LossEventWorkflowService : ILossEventWorkflowService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IAuditService _audit;
    private readonly Accounting.IInventoryLossAccountingAdapter? _lossAccounting;

    public LossEventWorkflowService(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        Accounting.IInventoryLossAccountingAdapter? lossAccounting = null)
    {
        _db = db;
        _stock = stock;
        _audit = audit;
        _lossAccounting = lossAccounting;
    }

    public LossEventComputation ComputeMetrics(
        decimal expectedQuantityMt,
        decimal actualQuantityMt,
        decimal toleranceQuantityMt)
    {
        var differenceQuantityMt = expectedQuantityMt - actualQuantityMt;
        var positiveLossMt = Math.Max(differenceQuantityMt, 0m);
        var allowableLossMt = Math.Min(positiveLossMt, toleranceQuantityMt);
        var chargeableLossMt = Math.Max(0m, positiveLossMt - toleranceQuantityMt);
        return new LossEventComputation(differenceQuantityMt, allowableLossMt, chargeableLossMt);
    }

    public async Task ValidateAsync(
        LossEventSubmission submission,
        Action<string, string> addError,
        CancellationToken ct = default)
    {
        NormalizeSubmission(submission);

        var product = await _db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == submission.ProductId && p.IsActive, ct);
        if (product is null)
        {
            addError(nameof(submission.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        if (submission.ContractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == submission.ContractId.Value, ct);
            if (contract is null)
            {
                addError(nameof(submission.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ProductId != submission.ProductId)
            {
                addError(nameof(submission.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
            }
        }

        if (submission.ShipmentId.HasValue)
        {
            var shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == submission.ShipmentId.Value, ct);
            if (shipment is null)
            {
                addError(nameof(submission.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
            else if (submission.ContractId.HasValue && shipment.ContractId != submission.ContractId)
            {
                addError(nameof(submission.ShipmentId), "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
            }
        }

        if (submission.LoadingRegisterId.HasValue)
        {
            var loading = await _db.LoadingRegisters
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == submission.LoadingRegisterId.Value, ct);
            if (loading is null)
            {
                addError(nameof(submission.LoadingRegisterId), "بارگیری انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (loading.ProductId != submission.ProductId)
                {
                    addError(nameof(submission.LoadingRegisterId), "بارگیری انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (submission.ContractId.HasValue && loading.ContractId != submission.ContractId)
                {
                    addError(nameof(submission.LoadingRegisterId), "بارگیری انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (submission.LoadingReceiptId.HasValue)
        {
            var receipt = await _db.LoadingReceipts
                .AsNoTracking()
                .Include(r => r.LoadingRegister)
                .FirstOrDefaultAsync(r => r.Id == submission.LoadingReceiptId.Value, ct);
            if (receipt is null)
            {
                addError(nameof(submission.LoadingReceiptId), "رسید بارگیری انتخاب‌شده معتبر نیست.");
            }
            else if (receipt.LoadingRegister is not null)
            {
                if (receipt.LoadingRegister.ProductId != submission.ProductId)
                {
                    addError(nameof(submission.LoadingReceiptId), "رسید بارگیری انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (submission.ContractId.HasValue && receipt.LoadingRegister.ContractId != submission.ContractId)
                {
                    addError(nameof(submission.LoadingReceiptId), "رسید بارگیری انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (submission.TruckDispatchId.HasValue)
        {
            var dispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == submission.TruckDispatchId.Value, ct);
            if (dispatch is null)
            {
                addError(nameof(submission.TruckDispatchId), "دیسپچ انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (dispatch.ProductId != submission.ProductId)
                {
                    addError(nameof(submission.TruckDispatchId), "دیسپچ انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (submission.ContractId.HasValue && dispatch.ContractId != submission.ContractId)
                {
                    addError(nameof(submission.TruckDispatchId), "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (submission.SalesTransactionId.HasValue)
        {
            var sale = await _db.SalesTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == submission.SalesTransactionId.Value, ct);
            if (sale is null)
            {
                addError(nameof(submission.SalesTransactionId), "فروش انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (sale.ProductId != submission.ProductId)
                {
                    addError(nameof(submission.SalesTransactionId), "فروش انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
                }

                if (submission.ContractId.HasValue && sale.ContractId != submission.ContractId)
                {
                    addError(nameof(submission.SalesTransactionId), "فروش انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
                }
            }
        }

        if (submission.AffectsInventory)
        {
            if (!CanAffectInventory(submission.Stage))
            {
                addError(nameof(submission.AffectsInventory), "این مرحله فقط برای گزارش است و نباید موجودی را دوباره کم کند.");
            }

            if (!submission.TerminalId.HasValue)
            {
                addError(nameof(submission.TerminalId), "برای ثبت اثر روی موجودی، انتخاب ترمینال الزامی است.");
            }

            if (!submission.StorageTankId.HasValue)
            {
                addError(nameof(submission.StorageTankId), "برای ثبت اثر روی موجودی، انتخاب مخزن الزامی است.");
            }
        }

        if (submission.TerminalId.HasValue)
        {
            var terminal = await _db.Terminals
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == submission.TerminalId.Value && t.IsActive, ct);
            if (terminal is null)
            {
                addError(nameof(submission.TerminalId), "ترمینال انتخاب‌شده معتبر نیست.");
            }
        }

        if (submission.StorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == submission.StorageTankId.Value, ct);
            if (tank is null)
            {
                addError(nameof(submission.StorageTankId), "مخزن انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (submission.TerminalId.HasValue && tank.TerminalId != submission.TerminalId.Value)
                {
                    addError(nameof(submission.StorageTankId), "مخزن انتخاب‌شده به ترمینال انتخابی تعلق ندارد.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != submission.ProductId)
                {
                    addError(nameof(submission.StorageTankId), "مخزن انتخاب‌شده برای کالای انتخابی تعریف نشده است.");
                }
            }
        }

        if (submission.ToleranceQuantityMt < 0m)
        {
            addError(nameof(submission.ToleranceQuantityMt), "تلورانس نامعتبر است.");
        }

        var metrics = ComputeMetrics(
            submission.ExpectedQuantityMt,
            submission.ActualQuantityMt,
            submission.ToleranceQuantityMt);
        var inventoryLossMt = Math.Max(metrics.DifferenceQuantityMt, 0m);

        if (submission.AffectsInventory && inventoryLossMt <= 0m)
        {
            addError(nameof(submission.AffectsInventory), "برای کاهش موجودی باید اختلاف مثبت باشد.");
        }
    }

    public async Task<LossEventWorkflowResult> CreateAsync(
        LossEventSubmission submission,
        CancellationToken ct = default)
    {
        var results = await CreateBatchAsync([submission], ct);
        return results[0];
    }

    /// <summary>
    /// ثبت گروهی رویدادهای ضایعات با تعداد ثابت SaveChanges (مستقل از تعداد ردیف).
    ///
    /// مسیر تک‌ردیفی <see cref="CreateAsync"/> هم از همین متد عبور می‌کند تا هرگز دو مسیر با
    /// رفتار متفاوت وجود نداشته باشد. ترتیب ورودی عیناً حفظ می‌شود، چون همان ترتیبِ منطقیِ
    /// تخصیص است و شناسه‌ها و ردیف‌های Audit بر اساس آن تولید می‌شوند.
    ///
    /// تعداد رفت‌وبرگشت‌ها ثابت است:
    ///   1) AddRange رویدادها و ذخیره، برای گرفتن LossEvent.Id که Audit و FK حرکت انبار به آن نیاز دارند.
    ///   2) فقط اگر حرکت انبار لازم باشد: AddRange حرکت‌ها و ذخیره، برای گرفتن InventoryMovement.Id.
    ///   3) ذخیره نهایی برای ردیف‌های Audit و مقداردهی LossEvent.InventoryMovementId.
    ///
    /// این متد تراکنش باز یا Commit نمی‌کند؛ در تراکنش فراخوان اجرا می‌شود و خطای هر ردیف
    /// باعث Rollback کل عملیات فراخوان می‌شود.
    /// </summary>
    public async Task<IReadOnlyList<LossEventWorkflowResult>> CreateBatchAsync(
        IReadOnlyList<LossEventSubmission> submissions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        if (submissions.Count == 0)
        {
            return Array.Empty<LossEventWorkflowResult>();
        }

        var pending = new List<PendingLossEvent>(submissions.Count);
        foreach (var submission in submissions)
        {
            pending.Add(await BuildPendingLossEventAsync(submission, ct));
        }

        _db.LossEvents.AddRange(pending.Select(p => p.Event));
        await _db.SaveChangesAsync(ct);

        var movementRows = pending.Where(p => p.Movement is not null).ToList();
        if (movementRows.Count > 0)
        {
            foreach (var row in movementRows)
            {
                row.Movement!.Notes = BuildInventoryNotes(
                    row.Submission.Stage,
                    $"LossEventId={row.Event.Id}"
                        + (string.IsNullOrWhiteSpace(row.Submission.Notes) ? string.Empty : $" | {row.Submission.Notes}"));
            }

            _db.InventoryMovements.AddRange(movementRows.Select(r => r.Movement!));
            await _db.SaveChangesAsync(ct);

            foreach (var row in movementRows)
            {
                row.Event.InventoryMovementId = row.Movement!.Id;
            }
        }

        foreach (var row in pending)
        {
            await WriteCreateAuditAsync(row, ct);
        }

        await _db.SaveChangesAsync(ct);

        var results = new List<LossEventWorkflowResult>(pending.Count);
        foreach (var row in pending)
        {
            if (_lossAccounting is not null)
            {
                await _lossAccounting.TryPostLossAsync(row.Event, ct);
            }

            results.Add(new LossEventWorkflowResult(row.Event, row.Movement, row.Metrics));
        }

        return results;
    }

    private sealed record PendingLossEvent(
        LossEventSubmission Submission,
        LossEvent Event,
        InventoryMovement? Movement,
        LossEventComputation Metrics);

    private async Task<PendingLossEvent> BuildPendingLossEventAsync(
        LossEventSubmission submission,
        CancellationToken ct)
    {
        NormalizeSubmission(submission);
        var metrics = ComputeMetrics(
            submission.ExpectedQuantityMt,
            submission.ActualQuantityMt,
            submission.ToleranceQuantityMt);
        var inventoryLossMt = Math.Max(metrics.DifferenceQuantityMt, 0m);

        InventoryMovement? movement = null;
        if (submission.AffectsInventory)
        {
            movement = new InventoryMovement
            {
                ProductId = submission.ProductId,
                ContractId = submission.ContractId,
                TerminalId = submission.TerminalId!.Value,
                StorageTankId = submission.StorageTankId,
                Direction = MovementDirection.Out,
                MovementDate = submission.EventDate,
                QuantityMt = inventoryLossMt,
                ReferenceDocument = submission.Reference ?? $"LOSS-{submission.EventDate:yyyyMMdd}",
                Notes = BuildInventoryNotes(submission.Stage, submission.Notes)
            };

            // قفل هم‌زمانی روی مخزن/کالا پیش از چک موجودی، تا دو ثبت هم‌زمانِ کسری روی یک مخزن
            // نتوانند هر دو از چک عبور کنند و بیش از موجودی خارج شود (داخل تراکنشِ فراخوان اجرا می‌شود).
            await _stock.AcquireStockMutationLockAsync(movement, ct);
            // کنترل کفایت موجودی به‌ازای هر حرکت باقی می‌ماند: یک قاعده کسب‌وکاری است که باید
            // اثر تجمعی حرکت‌های قبلی همین دسته را هم ببیند، پس دسته‌ای‌کردن آن رفتار را عوض می‌کند.
            await _stock.EnsureSufficientStockForMovementAsync(movement, ct);
        }

        var lossEvent = BuildLossEvent(submission, metrics);
        return new PendingLossEvent(submission, lossEvent, movement, metrics);
    }

    private static LossEvent BuildLossEvent(LossEventSubmission submission, LossEventComputation metrics)
    {
        return new LossEvent
        {
            Stage = submission.Stage,
            ProductId = submission.ProductId,
            ContractId = submission.ContractId,
            ShipmentId = submission.ShipmentId,
            LoadingRegisterId = submission.LoadingRegisterId,
            LoadingReceiptId = submission.LoadingReceiptId,
            TruckDispatchId = submission.TruckDispatchId,
            SalesTransactionId = submission.SalesTransactionId,
            TerminalId = submission.TerminalId,
            StorageTankId = submission.StorageTankId,
            EventDate = submission.EventDate,
            ExpectedQuantityMt = submission.ExpectedQuantityMt,
            ActualQuantityMt = submission.ActualQuantityMt,
            DifferenceQuantityMt = metrics.DifferenceQuantityMt,
            ToleranceQuantityMt = submission.ToleranceQuantityMt,
            AllowableLossMt = metrics.AllowableLossMt,
            ChargeableLossMt = metrics.ChargeableLossMt,
            ResponsiblePartyType = submission.ResponsiblePartyType,
            ResponsiblePartyName = submission.ResponsiblePartyName,
            FinancialTreatment = submission.FinancialTreatment,
            AffectsInventory = submission.AffectsInventory,
            LossCertainty = submission.LossCertainty,
            Reference = submission.Reference,
            Notes = submission.Notes
        };
    }

    private async Task WriteCreateAuditAsync(PendingLossEvent row, CancellationToken ct)
    {
        var submission = row.Submission;
        var lossEvent = row.Event;
        var movement = row.Movement;

        await _audit.LogAsync(
            nameof(LossEvent),
            lossEvent.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Stage", lossEvent.Stage),
                ("ProductId", lossEvent.ProductId),
                ("ContractId", lossEvent.ContractId),
                ("ShipmentId", lossEvent.ShipmentId),
                ("LoadingRegisterId", lossEvent.LoadingRegisterId),
                ("LoadingReceiptId", lossEvent.LoadingReceiptId),
                ("TruckDispatchId", lossEvent.TruckDispatchId),
                ("SalesTransactionId", lossEvent.SalesTransactionId),
                ("TerminalId", lossEvent.TerminalId),
                ("StorageTankId", lossEvent.StorageTankId),
                ("EventDate", lossEvent.EventDate),
                ("ExpectedQuantityMt", lossEvent.ExpectedQuantityMt),
                ("ActualQuantityMt", lossEvent.ActualQuantityMt),
                ("DifferenceQuantityMt", lossEvent.DifferenceQuantityMt),
                ("ToleranceQuantityMt", lossEvent.ToleranceQuantityMt),
                ("AllowableLossMt", lossEvent.AllowableLossMt),
                ("ChargeableLossMt", lossEvent.ChargeableLossMt),
                ("AffectsInventory", lossEvent.AffectsInventory),
                ("LossCertainty", lossEvent.LossCertainty),
                ("InventoryMovementId", lossEvent.InventoryMovementId),
                ("Reference", lossEvent.Reference)));

        if (movement is not null)
        {
            await _audit.LogAsync(
                nameof(InventoryMovement),
                movement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", movement.ProductId),
                    ("ContractId", movement.ContractId),
                    ("TerminalId", movement.TerminalId),
                    ("StorageTankId", movement.StorageTankId),
                    ("Direction", movement.Direction),
                    ("QuantityMt", movement.QuantityMt),
                    ("MovementDate", movement.MovementDate),
                    ("ReferenceDocument", movement.ReferenceDocument),
                    ("LossEventStage", submission.Stage)));
        }
    }

    private static bool CanAffectInventory(LossEventStage stage)
        => stage == LossEventStage.TankNaturalLoss
            || stage == LossEventStage.ManualAdjustment
            || stage == LossEventStage.TankFinalSettlement;

    private static void NormalizeSubmission(LossEventSubmission submission)
    {
        submission.Reference = TrimToNull(submission.Reference);
        submission.Notes = TrimToNull(submission.Notes);
        submission.ResponsiblePartyType = TrimToNull(submission.ResponsiblePartyType);
        submission.ResponsiblePartyName = TrimToNull(submission.ResponsiblePartyName);
        submission.FinancialTreatment = TrimToNull(submission.FinancialTreatment);
    }

    private static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildInventoryNotes(LossEventStage stage, string? notes)
    {
        var prefix = $"LossEvent trace | Stage={stage}";
        if (string.IsNullOrWhiteSpace(notes))
        {
            return prefix;
        }

        var combined = $"{prefix} | {notes.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }
}
