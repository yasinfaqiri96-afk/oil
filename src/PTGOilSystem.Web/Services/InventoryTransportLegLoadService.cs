using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تک‌منبع منطقِ «بارگیری یک تخصیص انتقال از موجودی»: ساخت حرکت خروجی، چک موجودیِ
/// مخزن/ترمینال منبع، و علامت‌گذاری leg به Loaded.
///
/// این سرویس هیچ تراکنشی باز نمی‌کند؛ caller باید همهٔ فراخوانی‌ها را داخل یک تراکنش
/// واحد بگذارد تا کل عملیات atomic بماند. خطاها به‌صورت <see cref="BusinessRuleException"/>
/// با پیام فارسی بالا می‌روند تا caller بتواند rollback کند و پیام انسانی نشان دهد.
///
/// هم مسیر قدیمی (<c>InventoryTransportLegsController.MarkLegsLoadedAsync</c>) و هم مسیر
/// جدیدِ «تخصیص از موجودی در ثبت محموله» از همین سرویس استفاده می‌کنند (بدون کپی منطق).
/// </summary>
public sealed class InventoryTransportLegLoadService
{
    public const string ReferencePrefix = "TRANSPORT-LEG";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IInventoryLineageWriter _lineage;
    private readonly IInventoryMovementWriter _movements;

    // Dual-write اختیاری به دفتر کل جدید — انتقال بهای موجودی به حساب «کالای در راه». پشت Feature Flag و null-safe.
    private readonly Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;

    // writer اختیاری است تا تمام call siteهای موجود (که سرویس را دستی new می‌کنند) بدون تغییر بمانند؛
    // اگر تزریق نشود، یک writerِ خاموش (WriteLots=false) ساخته می‌شود و رفتار دقیقاً مثل قبل است.
    public InventoryTransportLegLoadService(
        ApplicationDbContext db,
        IStockService stock,
        IInventoryLineageWriter? lineage = null,
        Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null,
        IInventoryMovementWriter? movements = null)
    {
        _db = db;
        _stock = stock;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _transferAccounting = transferAccounting;
        _movements = movements ?? new InventoryMovementWriter(db, stock);
    }

    /// <summary>
    /// یک تخصیص حمل را بارگیری می‌کند: اعتبارسنجی، چک موجودی، ساخت حرکت خروجی و
    /// تنظیم وضعیت leg به Loaded. باید داخل تراکنشِ caller فراخوانی شود.
    /// </summary>
    public async Task LoadAsync(InventoryTransportLeg leg)
    {
        await ValidateForLoadAsync(leg);

        // قفل هم‌زمانی روی مخزن مبدأ پیش از چک موجودی (داخل تراکنشِ caller)، تا دو بارگیریِ
        // هم‌زمان روی یک مخزن نتوانند هر دو از چک عبور کرده و موجودی را منفی کنند.
        // چک موجودی اینجا نسخهٔ مخصوص همین مسیر است (EnsureTankScopedStockAsync، عمداً بدون
        // asOfUtc)، پس نگهبان Available استاندارد Writer فراخوانی نمی‌شود.
        await _stock.AcquireStockMutationLockAsync(BuildOutboundMovement(leg));
        await EnsureTankScopedStockAsync(leg);

        var movement = await _movements.PostOutboundAsync(
            BuildOutboundRequest(leg),
            StockGuard.FutureTimeline);

        leg.OutboundInventoryMovementId = movement.Id;
        leg.Status = InventoryTransportLegStatus.Loaded;
        leg.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Dual-write داخل همان تراکنشِ caller: بهای بار از حوضچهٔ ترمینال مبدأ به «کالای در راه» می‌رود.
        if (_transferAccounting is not null)
        {
            await _transferAccounting.TryPostLegLoadAsync(leg);
        }

        // لایهٔ Lineage (پشت flag Lineage:WriteLots؛ با flag خاموش no-op). موجودی فیزیکی را تغییر نمی‌دهد.
        await _lineage.OnLegLoadedAsync(leg, movement);
    }

    public static InventoryMovementRequest BuildOutboundRequest(InventoryTransportLeg leg)
        => new()
        {
            ProductId = leg.ProductId,
            ContractId = leg.SourcePurchaseContractId,
            TerminalId = leg.SourceTerminalId,
            StorageTankId = leg.SourceStorageTankId,
            MovementDate = leg.LoadedDate,
            QuantityMt = leg.QuantityMt,
            ReferenceDocument = $"{ReferencePrefix}:{leg.Id}",
            Notes = "Inventory transport leg outbound movement"
        };

    // فقط برای قفل هم‌زمانی و تست‌های موجود؛ سند واقعی را Writer می‌سازد.
    public static InventoryMovement BuildOutboundMovement(InventoryTransportLeg leg)
        => new()
        {
            ProductId = leg.ProductId,
            ContractId = leg.SourcePurchaseContractId,
            TerminalId = leg.SourceTerminalId,
            StorageTankId = leg.SourceStorageTankId,
            Direction = MovementDirection.Out,
            MovementDate = leg.LoadedDate,
            QuantityMt = leg.QuantityMt,
            ReferenceDocument = $"{ReferencePrefix}:{leg.Id}",
            Notes = "Inventory transport leg outbound movement"
        };

    public async Task ValidateForLoadAsync(InventoryTransportLeg leg)
    {
        await EnsureSingleContractLegAsync(leg);

        if (leg.SourcePurchaseContract is null)
        {
            throw new BusinessRuleException("TRANSPORT_LEG_CONTRACT_MISSING", "Source purchase contract was not found.");
        }

        if (leg.SourcePurchaseContract.ContractType != ContractType.Purchase)
        {
            throw new BusinessRuleException("TRANSPORT_LEG_CONTRACT_NOT_PURCHASE", "Source contract must be a purchase contract.");
        }

        if (leg.SourcePurchaseContract.ProductId != leg.ProductId)
        {
            throw new BusinessRuleException("TRANSPORT_LEG_PRODUCT_MISMATCH", "Product must match the source purchase contract product.");
        }

        if (leg.QuantityMt <= 0m)
        {
            throw new BusinessRuleException("TRANSPORT_LEG_QTY_NON_POSITIVE", "Quantity must be greater than zero.");
        }

        if (!await _db.Products.AsNoTracking().AnyAsync(p => p.Id == leg.ProductId))
        {
            throw new BusinessRuleException("TRANSPORT_LEG_PRODUCT_MISSING", "Product was not found.");
        }

        if (!await _db.Terminals.AsNoTracking().AnyAsync(t => t.Id == leg.SourceTerminalId))
        {
            throw new BusinessRuleException("TRANSPORT_LEG_TERMINAL_MISSING", "Source terminal was not found.");
        }

        if (leg.SourceStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == leg.SourceStorageTankId.Value);
            if (tank is null)
            {
                throw new BusinessRuleException("TRANSPORT_LEG_TANK_MISSING", "Source tank was not found.");
            }

            if (tank.TerminalId != leg.SourceTerminalId)
            {
                throw new BusinessRuleException("TRANSPORT_LEG_TANK_TERMINAL_MISMATCH", "Source tank must belong to the selected source terminal.");
            }

            if (tank.ProductId.HasValue && tank.ProductId.Value != leg.ProductId)
            {
                throw new BusinessRuleException("TRANSPORT_LEG_TANK_PRODUCT_MISMATCH", "Source tank product does not match the selected product.");
            }
        }
    }

    /// <summary>
    /// این مسیر عمداً یک حرکت خروجیِ واحد برای کل مقدار با قرارداد سرصفحهٔ leg می‌سازد، پس
    /// فقط برای حملِ تک‌قراردادی معتبر است. حملی که سهم‌های منبع (<see cref="InventoryTransportLegAllocation"/>)
    /// دارد مسیر allocation-aware خودش را دارد (<c>InventoryTransportBatchService.LoadDraftAsync</c>)
    /// که برای هر سهم یک سند جدا می‌زند. اگر چنین حملی به این مسیر برسد، کلِ بار به قرارداد
    /// سرصفحه بسته می‌شود و موجودیِ قراردادی/شرکتیِ بقیهٔ سهم‌ها غلط می‌شود — پس به‌جای ساختِ
    /// بی‌صدای سند غلط، اینجا رد می‌شود.
    ///
    /// سهمِ تکی که دقیقاً همان قرارداد سرصفحه است رد نمی‌شود: سندی که ساخته می‌شود عیناً همان
    /// سندِ مسیر allocation-aware است.
    /// </summary>
    public async Task EnsureSingleContractLegAsync(InventoryTransportLeg leg)
    {
        if (leg.Id <= 0)
        {
            return;
        }

        var allocationContractIds = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.InventoryTransportLegId == leg.Id && a.QuantityMt > 0m)
            .Select(a => a.SourcePurchaseContractId)
            .Distinct()
            .ToListAsync();

        if (allocationContractIds.Count == 0)
        {
            return;
        }

        if (allocationContractIds.Count > 1
            || allocationContractIds[0] != leg.SourcePurchaseContractId)
        {
            throw new BusinessRuleException(
                "TRANSPORT_LEG_MULTI_ALLOCATION_LOAD_BLOCKED",
                $"حمل #{leg.Id} سهم منبع چندقراردادی دارد و از مسیر بارگیری تک‌قراردادی بارگیری نمی‌شود؛ باید از مسیر بارگیری پیش‌نویس دسته‌ای (allocation-aware) بارگیری شود.");
        }
    }

    public async Task EnsureTankScopedStockAsync(InventoryTransportLeg leg)
    {
        // عمداً بدون asOfUtc: بارگیریِ تاریخ‌گذشته با «موجودی فعلی» سنجیده می‌شود، نه با
        // موجودیِ تاریخِ بارگیری. این همان تصمیم عملیات است که رسیدِ دیرثبت‌شده نباید
        // بارگیریِ قبلاً انجام‌شده را بلاک کند
        // (تست: MarkLoaded_Uses_Current_Source_Stock_For_Backdated_Transport).
        var available = await _stock.GetFreeQuantityMtAsync(
            leg.ProductId,
            terminalId: leg.SourceTerminalId,
            contractId: leg.SourcePurchaseContractId,
            storageTankId: leg.SourceStorageTankId);

        if (available < leg.QuantityMt)
        {
            var shortage = leg.QuantityMt - available;
            var contractNumber = leg.SourcePurchaseContract?.ContractNumber ?? $"#{leg.SourcePurchaseContractId}";
            var tankLabel = leg.SourceStorageTankId.HasValue
                ? $"مخزن {leg.SourceStorageTank?.TankCode ?? "#" + leg.SourceStorageTankId.Value}"
                : "ترمینال منبع";
            throw new BusinessRuleException(
                "TRANSPORT_LEG_INSUFFICIENT_SOURCE_STOCK",
                $"موجودی کافی در {tankLabel} برای قرارداد {contractNumber} وجود ندارد. موجودی فعلی: {available:N4} MT، درخواست: {leg.QuantityMt:N4} MT، کمبود: {shortage:N4} MT.");
        }
    }
}
