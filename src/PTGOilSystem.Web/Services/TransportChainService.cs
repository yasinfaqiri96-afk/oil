using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>یک سهم از بارِ وسیلهٔ مقصد: چه مقدار، از کدام مرحلهٔ والد.</summary>
public sealed record ContinueToVehicleSource(int SourceLegId, decimal QuantityMt);

/// <summary>
/// یک انتقال «وسیله → وسیله». برای هر نُه ترکیب موتر/واگن/کشتی یک شکل دارد.
///
/// <para>چند منبع مجاز است: یک موتر می‌تواند از چند واگن پر شود. نتیجه همیشه <b>یک</b>
/// مرحلهٔ فرزند است با یک سهم به‌ازای هر والد — یعنی «یک بار ۱۰۰ تنی روی T1» نه دو بارِ جدا.</para>
/// </summary>
public sealed record ContinueToVehicleCommand
{
    /// <summary>مرحله‌های والد و سهم هرکدام. حداقل یکی.</summary>
    public required IReadOnlyList<ContinueToVehicleSource> Sources { get; init; }
    public required LoadingTransportType TargetTransportType { get; init; }
    public int? TargetTruckId { get; init; }
    public int? TargetWagonId { get; init; }
    public string? TargetWagonNumber { get; init; }
    public int? TargetVesselId { get; init; }
    public int? DriverId { get; init; }
    public required DateTime TransferDate { get; init; }
    public string? TicketSerialNumber { get; init; }
    public string? Notes { get; init; }

    /// <summary>مجموع سهم‌ها = بارِ وسیلهٔ مقصد.</summary>
    public decimal TotalQuantityMt => Sources.Sum(s => s.QuantityMt);
}

public sealed record ContinueToVehicleResult(
    InventoryTransportLeg ChildLeg,
    IReadOnlyList<InventoryTransportReceipt> SourceReceipts,
    IReadOnlyList<InventoryTransportLegAllocation> ChildAllocations)
{
    /// <summary>رسید مرحلهٔ والد اول — رکورد سازگاری موتر روی همین می‌نشیند.</summary>
    public InventoryTransportReceipt SourceReceipt => SourceReceipts[0];
}

public interface ITransportChainService
{
    Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// مرحله‌های فرزندی که این رسیدهای مبدأ ساخته‌اند را لغو می‌کند.
    /// اگر فرزندی خودش مصرف شده باشد (رسید فعال دارد) استثنا می‌اندازد تا زنجیره یتیم نشود.
    /// idempotent: فرزندِ از قبل لغوشده دوباره لغو نمی‌شود.
    /// </summary>
    Task<IReadOnlyList<InventoryTransportLeg>> CancelVehicleTransferAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default);
}

/// <summary>
/// موتور عمومی زنجیرهٔ حمل. تعویض وسیله مرز مخزن را رد نمی‌کند، پس:
///
/// <list type="bullet">
///   <item>هیچ <see cref="InventoryMovement"/> ساخته نمی‌شود — نه ورودی، نه خروجی.</item>
///   <item>مقدار از مرحلهٔ والد مصرف می‌شود (یک رسیدِ DirectDispatch) تا نگهداشت مقدار برقرار بماند.</item>
///   <item>یک مرحلهٔ فرزند با همان هویت حمل (Batch/GroupKey والد) ساخته می‌شود.</item>
///   <item>سهم قراردادهای منبع به‌تناسب به فرزند منتقل می‌شود.</item>
/// </list>
///
/// <para><b>Split و Merge:</b> هر دو از یک ساختار می‌آیند. سهم‌های منبع روی
/// <see cref="InventoryTransportLegAllocation"/> می‌نشینند که از قبل یک مجموعهٔ چندتایی است:
/// یک والد در سهم‌های چند فرزند ظاهر می‌شود (Split)، و یک فرزند سهم‌هایی با والدهای متفاوت
/// می‌گیرد (Merge). هیچ ستون Parent تکی این را نمی‌توانست بیان کند.</para>
///
/// <para><b>مالکیت تراکنش:</b> این سرویس تراکنش باز نمی‌کند؛ caller مالک است — همان قرارداد
/// <see cref="InventoryMovementWriter"/>.</para>
/// </summary>
public sealed class TransportChainService : ITransportChainService
{
    private const decimal Epsilon = 0.0001m;

    private readonly ApplicationDbContext _db;
    private readonly InventoryTransportReceiptService _receipts;
    private readonly ITransportQuantityService _quantities;

    public TransportChainService(
        ApplicationDbContext db,
        InventoryTransportReceiptService receipts,
        ITransportQuantityService quantities)
    {
        _db = db;
        _receipts = receipts;
        _quantities = quantities;
    }

    public async Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Sources.Count == 0)
        {
            throw new BusinessRuleException(
                "TRANSPORT_CHAIN_NO_SOURCE",
                "حداقل یک حمل مبدأ لازم است.");
        }

        await ValidateTargetAsync(command, ct);

        // هر سهم پیش از ساختِ چیزی اعتبارسنجی می‌شود تا یک سهم نامعتبر، رکورد نیمه‌ساخته جا نگذارد.
        var validated = new List<(InventoryTransportLeg Leg, decimal QuantityMt)>(command.Sources.Count);
        var sourceIds = new HashSet<int>();
        int? productId = null;
        foreach (var source in command.Sources)
        {
            if (!sourceIds.Add(source.SourceLegId))
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_SOURCE_DUPLICATE",
                    "یک حمل مبدأ در فرمان انتقال تکرار شده است.");
            }
            if (source.QuantityMt <= 0m)
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_QTY_NON_POSITIVE",
                    "مقدار انتقال باید بزرگ‌تر از صفر باشد.");
            }

            var sourceLeg = await _receipts.LoadLegAsync(source.SourceLegId, tracking: true)
                ?? throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_SOURCE_MISSING",
                    $"حمل مبدأ #{source.SourceLegId} یافت نشد.");

            if (sourceLeg.Status is InventoryTransportLegStatus.Cancelled or InventoryTransportLegStatus.Draft)
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_SOURCE_NOT_TRANSFERABLE",
                    "حمل مبدأ در وضعیتی نیست که بارش قابل انتقال باشد.");
            }
            if (productId.HasValue && productId.Value != sourceLeg.ProductId)
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_PRODUCT_MISMATCH",
                    "ادغام فقط میان حمل‌های یک محصول ممکن است.");
            }
            productId ??= sourceLeg.ProductId;

            // نگهداشت مقدار: انتقال هرگز از باقیماندهٔ واقعی والد بیشتر نمی‌شود. همین نگهبان
            // ارسال دوباره را هم بی‌اثر می‌کند، چون رسید اول باقیمانده را پایین آورده است.
            var remainingMt = await _quantities.GetRemainingMtAsync(sourceLeg.Id, ct);
            if (source.QuantityMt > remainingMt + Epsilon)
            {
                throw new BusinessRuleException(
                    "TRANSPORT_CHAIN_QTY_EXCEEDS_REMAINING",
                    $"مقدار انتقال ({source.QuantityMt:N4} MT) از باقیماندهٔ حمل #{sourceLeg.Id} ({remainingMt:N4} MT) بیشتر است.");
            }

            validated.Add((sourceLeg, source.QuantityMt));
        }

        var totalQuantityMt = command.TotalQuantityMt;
        var firstLeg = validated[0].Leg;
        var firstShares = await BuildSourceSharesAsync(firstLeg, validated[0].QuantityMt, ct);

        // یک وسیلهٔ مقصد = یک مرحلهٔ فرزند، حتی وقتی از چند والد پر شده باشد. فرزند زیر همان
        // هویت حملِ والد اول می‌نشیند تا کل زنجیره با یک شناسه Trace شود.
        var childLeg = new InventoryTransportLeg
        {
            InventoryTransportBatchId = firstLeg.InventoryTransportBatchId,
            ShipmentId = firstLeg.ShipmentId,
            TransportGroupKey = firstLeg.TransportGroupKey,
            SourcePurchaseContractId = firstShares[0].ContractId,
            ProductId = firstLeg.ProductId,
            // مبدأ فیزیکی فرزند همان جایی است که والد از آن آمده؛ کالا وارد مخزنی نشده.
            SourceTerminalId = firstLeg.SourceTerminalId,
            SourceStorageTankId = firstLeg.SourceStorageTankId,
            TransportType = command.TargetTransportType,
            TruckId = command.TargetTransportType == LoadingTransportType.Truck ? command.TargetTruckId : null,
            WagonId = command.TargetTransportType == LoadingTransportType.Wagon ? command.TargetWagonId : null,
            VesselId = command.TargetTransportType == LoadingTransportType.Vessel ? command.TargetVesselId : null,
            WagonNumber = command.TargetTransportType == LoadingTransportType.Wagon ? command.TargetWagonNumber : null,
            DriverId = command.DriverId,
            LoadedDate = command.TransferDate.Date,
            QuantityMt = totalQuantityMt,
            Status = InventoryTransportLegStatus.Loaded,
            Notes = command.Notes
        };

        _db.InventoryTransportLegs.Add(childLeg);
        await _db.SaveChangesAsync(ct);

        var receipts = new List<InventoryTransportReceipt>(validated.Count);
        var childAllocations = new List<InventoryTransportLegAllocation>();

        for (var i = 0; i < validated.Count; i++)
        {
            var (sourceLeg, quantityMt) = validated[i];
            var isPrimary = i == 0;
            var shares = isPrimary ? firstShares : await BuildSourceSharesAsync(sourceLeg, quantityMt, ct);

            // مصرف والد از همان موتور رسید عبور می‌کند (قلاب‌های حسابداری/نسب‌نامه، کرایه، کسری).
            // ReceivedQuantityMt در این مقصد یعنی «چقدر از والد به وسیلهٔ بعدی رفت» و هیچ حرکت
            // موجودی نمی‌سازد — ApplyAsync فقط برای مقصد ToInventory سند In می‌زند.
            //
            // رکورد سازگاری موتر فقط یک بار و با وزن کاملِ وسیله ساخته می‌شود؛ سهم‌های بعدی
            // رسیدهای «همراه» هستند و با یادداشت نشانه‌دار به رسید اصلی وصل می‌شوند تا لغو
            // گروهی و فهرست انتقال‌ها دقیقاً مثل قبل کار کنند.
            var notes = isPrimary
                ? command.Notes
                : BuildCompanionNotes(receipts[0].Id, command.Notes);

            var receipt = await _receipts.ApplyAsync(
                new InventoryTransportReceiptCreateViewModel
                {
                    InventoryTransportLegId = sourceLeg.Id,
                    ReceiptDate = command.TransferDate.Date,
                    ReceivedQuantityMt = quantityMt,
                    ShortageQuantityMt = 0m,
                    ReceiptDestination = InventoryTransportReceiptDestination.DirectDispatch,
                    DirectDispatchTransportType = command.TargetTransportType,
                    DirectDispatchTruckId = command.TargetTruckId,
                    DirectDispatchWagonId = command.TargetWagonId,
                    DirectDispatchWagonNumber = command.TargetWagonNumber,
                    DirectDispatchVesselId = command.TargetVesselId,
                    DirectDispatchDriverId = command.DriverId,
                    DirectDispatchDate = command.TransferDate.Date,
                    DirectDispatchLoadedQuantityMt = isPrimary ? totalQuantityMt : quantityMt,
                    AllowDirectDispatchBeyondReceipt = isPrimary && validated.Count > 1,
                    SkipDirectDispatchRecord = !isPrimary,
                    DirectDispatchTicketSerialNumber = command.TicketSerialNumber,
                    Notes = notes
                },
                sourceLeg,
                saleConversion: null);

            receipts.Add(receipt);

            childAllocations.AddRange(shares.Select(share => new InventoryTransportLegAllocation
            {
                InventoryTransportLegId = childLeg.Id,
                SourcePurchaseContractId = share.ContractId,
                SourceLoadingReceiptId = share.LoadingReceiptId,
                // منبع وسیله است، نه مخزن — پس هیچ سند موجودی مبدأ ندارد.
                SourceInventoryMovementId = null,
                SourceTransportLegId = sourceLeg.Id,
                SourceTransportReceiptId = receipt.Id,
                QuantityMt = share.QuantityMt
            }));
        }

        _db.InventoryTransportLegAllocations.AddRange(childAllocations);
        await _db.SaveChangesAsync(ct);

        return new ContinueToVehicleResult(childLeg, receipts, childAllocations);
    }

    private async Task ValidateTargetAsync(ContinueToVehicleCommand command, CancellationToken ct)
    {
        switch (command.TargetTransportType)
        {
            case LoadingTransportType.Truck:
                if (!command.TargetTruckId.HasValue
                    || !await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == command.TargetTruckId && t.IsActive, ct))
                {
                    throw new BusinessRuleException("TRANSPORT_CHAIN_TRUCK_INVALID", "موتر مقصد معتبر و فعال نیست.");
                }
                break;
            case LoadingTransportType.Wagon:
                if (command.TargetWagonId.HasValue)
                {
                    if (!await _db.Wagons.AsNoTracking().AnyAsync(w => w.Id == command.TargetWagonId && w.IsActive, ct))
                    {
                        throw new BusinessRuleException("TRANSPORT_CHAIN_WAGON_INVALID", "واگن مقصد معتبر و فعال نیست.");
                    }
                }
                else if (string.IsNullOrWhiteSpace(command.TargetWagonNumber))
                {
                    throw new BusinessRuleException("TRANSPORT_CHAIN_WAGON_REQUIRED", "واگن مقصد را انتخاب کنید.");
                }
                break;
            case LoadingTransportType.Vessel:
                if (!command.TargetVesselId.HasValue
                    || !await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == command.TargetVesselId && v.IsActive, ct))
                {
                    throw new BusinessRuleException("TRANSPORT_CHAIN_VESSEL_INVALID", "کشتی مقصد معتبر و فعال نیست.");
                }
                break;
            default:
                throw new BusinessRuleException("TRANSPORT_CHAIN_TYPE_INVALID", "نوع وسیله مقصد معتبر نیست.");
        }

        if (command.DriverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == command.DriverId && d.IsActive, ct))
        {
            throw new BusinessRuleException("TRANSPORT_CHAIN_DRIVER_INVALID", "راننده انتخاب‌شده معتبر و فعال نیست.");
        }
    }

    public async Task<IReadOnlyList<InventoryTransportLeg>> CancelVehicleTransferAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReceiptIds);

        if (sourceReceiptIds.Count == 0)
        {
            return [];
        }

        // مرحله‌های فرزند از روی سهم‌هایی پیدا می‌شوند که رسیدِ مبدأشان لغو می‌شود.
        var childLegIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceTransportReceiptId != null
                && sourceReceiptIds.Contains(a.SourceTransportReceiptId.Value))
            .Select(a => a.InventoryTransportLegId)
            .Distinct()
            .ToListAsync(ct);

        if (childLegIds.Count == 0)
        {
            // انتقال‌های پیش از مدل زنجیره فرزندی ندارند؛ لغوشان دقیقاً مثل قبل کار می‌کند.
            return [];
        }

        var childLegs = await _db.InventoryTransportLegs
            .Where(l => childLegIds.Contains(l.Id))
            .ToListAsync(ct);

        // نگهبان پایین‌دست: اگر خودِ فرزند بارش را جای دیگری برده (وسیلهٔ بعدی، مخزن، فروش)،
        // لغو این مرحله آن عملیات را یتیم می‌گذارد. اول باید مرحلهٔ بعدی لغو شود.
        var consumedChildIds = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => childLegIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .Select(r => r.InventoryTransportLegId)
            .Distinct()
            .ToListAsync(ct);

        if (consumedChildIds.Count > 0)
        {
            var labels = string.Join("، ", consumedChildIds.Select(id => $"#{id}"));
            throw new BusinessRuleException(
                "TRANSPORT_CHAIN_CHILD_HAS_DOWNSTREAM",
                $"برای مرحلهٔ بعدی این حمل ({labels}) عملیات ثبت شده است؛ ابتدا مرحلهٔ بعدی را لغو کنید.");
        }

        var cancelled = new List<InventoryTransportLeg>(childLegs.Count);
        foreach (var childLeg in childLegs)
        {
            // idempotent: لغو دوباره وضعیت را عوض نمی‌کند و رکورد تازه نمی‌سازد.
            if (childLeg.Status == InventoryTransportLegStatus.Cancelled)
            {
                continue;
            }

            childLeg.Status = InventoryTransportLegStatus.Cancelled;
            childLeg.UpdatedAtUtc = DateTime.UtcNow;
            cancelled.Add(childLeg);
        }

        // سهم‌های فرزند حذف فیزیکی نمی‌شوند: تاریخچه باید قابل ردیابی بماند و مصرفِ والد
        // از رسیدهای فعال حساب می‌شود، نه از سهم‌های فرزند. پس لغوِ مرحله کافی است.
        await _db.SaveChangesAsync(ct);
        return cancelled;
    }

    /// <summary>
    /// یادداشت رسیدِ «همراه»: سهم‌های دوم به بعدِ یک وسیلهٔ چندمنبعی به رسید اصلی وصل می‌شوند.
    /// فهرست انتقال‌ها و لغو گروهی روی همین نشانه کار می‌کنند، پس قالبش نباید عوض شود.
    /// </summary>
    public const string CompanionReceiptNotePrefix = "[انتقال همراه رسید #";

    private static string BuildCompanionNotes(int primaryReceiptId, string? notes)
    {
        var marker = $"{CompanionReceiptNotePrefix}{primaryReceiptId}]";
        return string.IsNullOrWhiteSpace(notes) ? marker : $"{marker} {notes.Trim()}";
    }

    /// <summary>
    /// سهم قراردادهای منبع برای مقدار منتقل‌شده، به تناسب سهم‌های خودِ مرحلهٔ والد.
    /// همان قاعده‌ای که تقسیم رسیدِ چندقراردادی استفاده می‌کند، تا یک بار مصرف در دو جای
    /// زنجیره دو جواب متفاوت ندهد. باقیماندهٔ گِرد به بزرگ‌ترین سهم می‌رود.
    /// </summary>
    private async Task<IReadOnlyList<SourceShare>> BuildSourceSharesAsync(
        InventoryTransportLeg sourceLeg,
        decimal quantityMt,
        CancellationToken ct)
    {
        var allocations = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == sourceLeg.Id)
            .GroupBy(a => new { a.SourcePurchaseContractId, a.SourceLoadingReceiptId })
            .Select(g => new
            {
                g.Key.SourcePurchaseContractId,
                g.Key.SourceLoadingReceiptId,
                QuantityMt = g.Sum(a => a.QuantityMt)
            })
            .ToListAsync(ct);

        var totalMt = allocations.Sum(a => a.QuantityMt);

        // حمل‌های قدیمی بدون سهم ثبت‌شده: قرارداد سرصفحه، دقیقاً رفتار امروز.
        if (allocations.Count == 0 || totalMt <= 0m)
        {
            return [new SourceShare(sourceLeg.SourcePurchaseContractId, null, quantityMt)];
        }

        var ordered = allocations
            .OrderByDescending(a => a.QuantityMt)
            .ThenBy(a => a.SourcePurchaseContractId)
            .ToList();

        if (ordered.Count == 1)
        {
            return [new SourceShare(ordered[0].SourcePurchaseContractId, ordered[0].SourceLoadingReceiptId, quantityMt)];
        }

        var shares = new List<SourceShare>(ordered.Count);
        var assignedMt = 0m;
        for (var i = 1; i < ordered.Count; i++)
        {
            var shareMt = decimal.Round(
                quantityMt * ordered[i].QuantityMt / totalMt,
                4,
                MidpointRounding.AwayFromZero);
            if (shareMt <= 0m)
            {
                continue;
            }

            shares.Add(new SourceShare(ordered[i].SourcePurchaseContractId, ordered[i].SourceLoadingReceiptId, shareMt));
            assignedMt += shareMt;
        }

        shares.Insert(0, new SourceShare(
            ordered[0].SourcePurchaseContractId,
            ordered[0].SourceLoadingReceiptId,
            quantityMt - assignedMt));
        return shares;
    }

    private sealed record SourceShare(int ContractId, int? LoadingReceiptId, decimal QuantityMt);
}
