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

    // Dual-write اختیاری به دفتر کل جدید — انتقال بهای موجودی به حساب «کالای در راه». پشت Feature Flag و null-safe.
    private readonly Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;

    // writer اختیاری است تا تمام call siteهای موجود (که سرویس را دستی new می‌کنند) بدون تغییر بمانند؛
    // اگر تزریق نشود، یک writerِ خاموش (WriteLots=false) ساخته می‌شود و رفتار دقیقاً مثل قبل است.
    public InventoryTransportLegLoadService(
        ApplicationDbContext db,
        IStockService stock,
        IInventoryLineageWriter? lineage = null,
        Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null)
    {
        _db = db;
        _stock = stock;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _transferAccounting = transferAccounting;
    }

    /// <summary>
    /// یک تخصیص حمل را بارگیری می‌کند: اعتبارسنجی، چک موجودی، ساخت حرکت خروجی و
    /// تنظیم وضعیت leg به Loaded. باید داخل تراکنشِ caller فراخوانی شود.
    /// </summary>
    public async Task LoadAsync(InventoryTransportLeg leg)
    {
        await ValidateForLoadAsync(leg);

        var movement = BuildOutboundMovement(leg);

        // قفل هم‌زمانی روی مخزن مبدأ پیش از چک موجودی (داخل تراکنشِ caller)، تا دو بارگیریِ
        // هم‌زمان روی یک مخزن نتوانند هر دو از چک عبور کرده و موجودی را منفی کنند.
        await _stock.AcquireStockMutationLockAsync(movement);
        await EnsureTankScopedStockAsync(leg);
        await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(movement);

        _db.InventoryMovements.Add(movement);
        await _db.SaveChangesAsync();

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
