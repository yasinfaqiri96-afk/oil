using System.Data;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// درگاه واحد نوشتن Workflow حمل. این Facade منطق موتورهای تخصصی را دوباره پیاده نمی‌کند؛
/// فقط فرمان کاربر را به سرویس موجودی، زنجیره، رسید/فروش و کسری هدایت می‌کند.
/// </summary>
public interface ITransportWorkflowService
{
    Task<InventoryTransportBatch> StartFromInventoryAsync(
        InventoryTransportFromInventoryViewModel model,
        string? formToken,
        CancellationToken ct = default);

    Task<InventoryTransportLeg> StartFromReceiptAsync(
        StartTransportFromReceiptCommand command,
        CancellationToken ct = default);

    Task<InventoryTransportLeg> StartFromLoadingAsync(
        StartTransportFromLoadingCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// تبدیل گروهی چند بارگیری به حمل. قواعد دقیقاً همان مسیر تکی است — همان سازندهٔ سند،
    /// همان فرمول باقیمانده، همان اعتبارسنجی وسیله — فقط خواندن‌ها و نوشتن‌ها دسته‌ای می‌شوند.
    /// </summary>
    Task<BulkStartTransportFromLoadingResult> StartManyFromLoadingAsync(
        BulkStartTransportFromLoadingCommand command,
        CancellationToken ct = default);

    Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> ReceiveToInventoryAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> SellQuantityAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion,
        CancellationToken ct = default);

    Task<LossEventWorkflowResult> RecordLossAsync(
        LossEventSubmission submission,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> SettleFreightAsync(
        SettleTransportFreightCommand command,
        CancellationToken ct = default);

    Task<IReadOnlyList<InventoryTransportLeg>> CancelOrReverseAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default);
}

public sealed record StartTransportFromReceiptCommand
{
    public required int LoadingReceiptId { get; init; }
    public required decimal QuantityMt { get; init; }
    public required LoadingTransportType TransportType { get; init; }
    public int? TruckId { get; init; }
    public int? WagonId { get; init; }
    public int? VesselId { get; init; }
    public int? DriverId { get; init; }
    public int? ServiceProviderId { get; init; }
    public required DateTime TransportDate { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}

public sealed record StartTransportFromLoadingCommand
{
    public required int LoadingRegisterId { get; init; }
    public required decimal QuantityMt { get; init; }
    public required LoadingTransportType TransportType { get; init; }
    public int? TruckId { get; init; }
    public int? WagonId { get; init; }
    public int? VesselId { get; init; }
    public int? DriverId { get; init; }
    public int? ServiceProviderId { get; init; }
    public required DateTime TransportDate { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}

/// <summary>یک ردیفِ تبدیل گروهی؛ دقیقاً همان دادهٔ فرمِ تک‌بارگیری.</summary>
public sealed record BulkStartTransportFromLoadingRow
{
    public required int LoadingRegisterId { get; init; }
    public required decimal QuantityMt { get; init; }
    public required LoadingTransportType TransportType { get; init; }
    public int? TruckId { get; init; }
    public int? WagonId { get; init; }
    public int? VesselId { get; init; }
    public int? DriverId { get; init; }
    public int? ServiceProviderId { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }

    /// <summary>برچسبِ ردیف فقط برای پیام خطا؛ هیچ اثری در ثبت ندارد.</summary>
    public string? Label { get; init; }
}

public sealed record BulkStartTransportFromLoadingCommand
{
    public required IReadOnlyList<BulkStartTransportFromLoadingRow> Rows { get; init; }
    public required DateTime TransportDate { get; init; }

    /// <summary>
    /// تعداد ردیف در هر تراکنش. هر دسته یک تراکنش Serializable است؛ کوچک‌نگه‌داشتن آن
    /// مدتِ قفلِ سطرهای بارگیری را کوتاه و ChangeTracker را کرانمند نگه می‌دارد.
    /// </summary>
    public int ChunkSize { get; init; } = DefaultChunkSize;

    public const int DefaultChunkSize = 200;

    /// <summary>توکن ضدتکراری فرم؛ در اولین دستهٔ موفق مصرف می‌شود.</summary>
    public string? FormToken { get; init; }
}

public sealed record BulkStartTransportFromLoadingFailure(
    int LoadingRegisterId,
    string? Label,
    string Code,
    string Message);

public sealed record BulkStartTransportFromLoadingResult
{
    public required IReadOnlyList<int> CreatedLegIds { get; init; }
    public required IReadOnlyList<BulkStartTransportFromLoadingFailure> Failures { get; init; }
    public int CreatedCount => CreatedLegIds.Count;
}

public sealed record SettleTransportFreightCommand
{
    public required int TransportLegId { get; init; }
    public required DateTime SettlementDate { get; init; }
    public decimal? FreightRateUsdPerMt { get; init; }
    public decimal? FreightCostUsd { get; init; }

    /// <summary>
    /// رانندهٔ طرفِ کرایه، وقتی حمل شرکت خدماتی/دارایی ملکی ندارد. روی خودِ حمل ثبت
    /// می‌شود تا سرویس رسید همان قاعدهٔ همیشگیِ «شرکت خدماتی، وگرنه راننده» را ببیند و
    /// بدهیِ کرایه روی حساب همان راننده بنشیند.
    /// </summary>
    public int? DriverId { get; init; }

    public string? Notes { get; init; }
}

public sealed class TransportWorkflowService : ITransportWorkflowService
{
    private const decimal Epsilon = 0.0001m;

    private readonly ApplicationDbContext _db;
    private readonly InventoryTransportBatchService _inventoryStarts;
    private readonly ITransportChainService _chain;
    private readonly InventoryTransportReceiptService _outcomes;
    private readonly ILossEventWorkflowService _losses;

    // اختیاری تا ساخت مستقیمِ سرویس در تست‌ها دست‌نخورده بماند؛ نبودش یعنی بدون محافظ
    // ضدتکراری (fail-open)، دقیقاً مثل بقیهٔ مسیرهای پروژه.
    private readonly IFormTokenGuard? _formTokens;

    public TransportWorkflowService(
        ApplicationDbContext db,
        InventoryTransportBatchService inventoryStarts,
        ITransportChainService chain,
        InventoryTransportReceiptService outcomes,
        ILossEventWorkflowService losses,
        IFormTokenGuard? formTokens = null)
    {
        _db = db;
        _inventoryStarts = inventoryStarts;
        _chain = chain;
        _outcomes = outcomes;
        _losses = losses;
        _formTokens = formTokens;
    }

    public Task<InventoryTransportBatch> StartFromInventoryAsync(
        InventoryTransportFromInventoryViewModel model,
        string? formToken,
        CancellationToken ct = default)
        => _inventoryStarts.CreateAsync(model, formToken, ct);

    public async Task<InventoryTransportLeg> StartFromLoadingAsync(
        StartTransportFromLoadingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.QuantityMt <= 0m)
        {
            throw Rule("TRANSPORT_LOADING_QTY_INVALID", "مقدار حمل باید بزرگ‌تر از صفر باشد.");
        }
        if (command.TransportDate == default)
        {
            throw Rule("TRANSPORT_LOADING_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        await ValidateVehicleAsync(
            command.TransportType,
            command.TruckId,
            command.WagonId,
            command.VesselId,
            command.DriverId,
            command.ServiceProviderId,
            ct);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            LoadingRegister? loading;
            if (_db.Database.IsRelational()
                && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                loading = await _db.LoadingRegisters
                    .FromSqlInterpolated($@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = {command.LoadingRegisterId} FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                loading = await _db.LoadingRegisters.SingleOrDefaultAsync(l => l.Id == command.LoadingRegisterId, ct);
            }

            if (loading is null)
            {
                throw Rule("TRANSPORT_LOADING_NOT_FOUND", "بارگیری انتخاب‌شده پیدا نشد.");
            }

            var receivedMt = await _db.LoadingReceipts
                .AsNoTracking()
                .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
                .SumAsync(r => (decimal?)r.ReceivedQuantityMt, ct) ?? 0m;
            var shortageMt = await _db.LossEvents
                .AsNoTracking()
                .Where(e => (e.LoadingRegisterId == loading.Id
                        || e.LoadingReceiptId.HasValue
                            && e.LoadingReceipt != null
                            && e.LoadingReceipt.LoadingRegisterId == loading.Id)
                    && e.Stage == LossEventStage.ReceiptShortage
                    && !e.IsCancelled)
                .SumAsync(e => (decimal?)(e.DifferenceQuantityMt > 0m
                    ? e.DifferenceQuantityMt
                    : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m), ct) ?? 0m;
            var transportedMt = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => a.SourceLoadingRegisterId == loading.Id
                    && a.InventoryTransportLeg != null
                    && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                .SumAsync(a => (decimal?)a.QuantityMt, ct) ?? 0m;
            var availableMt = AvailableFromLoadingMt(loading.LoadedQuantityMt, receivedMt, shortageMt, transportedMt);
            if (command.QuantityMt > availableMt + Epsilon)
            {
                throw InsufficientLoading(availableMt);
            }

            var draft = new LoadingConversionDraft
            {
                QuantityMt = command.QuantityMt,
                TransportType = command.TransportType,
                TruckId = command.TruckId,
                WagonId = command.WagonId,
                VesselId = command.VesselId,
                DriverId = command.DriverId,
                ServiceProviderId = command.ServiceProviderId,
                TransportDate = command.TransportDate,
                Reference = command.Reference,
                Notes = command.Notes
            };
            var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
                draft.ServiceProviderId,
                draft.TransportType == LoadingTransportType.Truck ? draft.DriverId : null,
                operationalAssetId: null,
                draft.TransportDate.Date,
                ct);
            var batch = BuildLoadingConversion(loading, draft, carrierParty);
            var leg = batch.Legs.Single();
            _db.InventoryTransportBatches.Add(batch);
            await _db.SaveChangesAsync(ct);
            await new AssetUsageChargeService(_db).SyncOperationAsync(leg, ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return leg;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    // ───────────────────── تبدیل گروهی بارگیری‌ها به حمل ─────────────────────
    //
    // مسیر قبلی برای هر بارگیری یک بار کل سرویس تکی را صدا می‌زد: ۸ رفت‌وبرگشت و یک تراکنش
    // Serializable در هر ردیف، و چون همهٔ سطرهای ساخته‌شده در ChangeTracker می‌ماندند هزینهٔ
    // هر SaveChanges با تعداد ردیف‌ها بالا می‌رفت (اندازه‌گیری‌شده: N=500 → ۹۹ ثانیه، و با
    // خالی‌کردن ChangeTracker همان کار ۵٫۷ ثانیه).
    //
    // مسیر گروهی همان قواعد را نگه می‌دارد و فقط شکل اجرا را عوض می‌کند:
    //   • اعتبارسنجی وسیله یک‌بار برای کل فرمان، نه یک‌بار در هر ردیف؛
    //   • هر دسته یک تراکنش، با یک قفلِ گروهیِ FOR UPDATE و سه کوئری تجمیعی؛
    //   • یک SaveChanges در هر دسته و خالی‌کردن ChangeTracker بعد از هر دسته.
    //
    // معنای Partial Success دست‌نخورده می‌ماند: اگر یک دسته شکست بخورد، همان دسته ردیف‌به‌ردیف
    // از مسیر تکی اجرا می‌شود تا فقط ردیفِ مقصر رد شود و بقیه ثبت شوند.
    public async Task<BulkStartTransportFromLoadingResult> StartManyFromLoadingAsync(
        BulkStartTransportFromLoadingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TransportDate == default)
        {
            throw Rule("TRANSPORT_LOADING_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        var createdLegIds = new List<int>();
        var failures = new List<BulkStartTransportFromLoadingFailure>();
        var rows = command.Rows ?? [];
        if (rows.Count == 0)
        {
            return new BulkStartTransportFromLoadingResult
            {
                CreatedLegIds = createdLegIds,
                Failures = failures
            };
        }

        // ۱) اعتبارسنجیِ بی‌نیاز از دیتابیس + اعتبارسنجیِ وسیله به‌صورت مجموعه‌ای (۵ کوئری برای کل فرمان).
        var vehicles = await LoadActiveVehicleSetsAsync(rows, ct);
        var accepted = new List<BulkStartTransportFromLoadingRow>(rows.Count);
        foreach (var row in rows)
        {
            var failure = ValidateBulkRow(row, vehicles);
            if (failure is not null)
            {
                failures.Add(failure);
                continue;
            }
            accepted.Add(row);
        }

        // ۲) دسته‌دسته. هر دسته یک تراکنش و یک SaveChanges.
        var chunkSize = command.ChunkSize > 0
            ? command.ChunkSize
            : BulkStartTransportFromLoadingCommand.DefaultChunkSize;
        var tokenPending = !string.IsNullOrWhiteSpace(command.FormToken);
        for (var offset = 0; offset < accepted.Count; offset += chunkSize)
        {
            var chunk = accepted.GetRange(offset, Math.Min(chunkSize, accepted.Count - offset));
            var stampToken = tokenPending ? command.FormToken : null;
            try
            {
                var legIds = await ConvertChunkAsync(chunk, command.TransportDate, stampToken, ct);
                createdLegIds.AddRange(legIds);
                if (stampToken is not null)
                {
                    tokenPending = false;
                }
            }
            catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
            {
                // ثبتِ تکراریِ همان فرم — نباید با تلاش دوباره حملِ تکراری ساخته شود.
                throw;
            }
            catch
            {
                // دسته شکست خورد؛ همان ردیف‌ها را تکی اجرا می‌کنیم تا فقط ردیفِ مقصر رد شود.
                _db.ChangeTracker.Clear();
                await ConvertChunkRowByRowAsync(chunk, command.TransportDate, createdLegIds, failures, ct);
            }

            _db.ChangeTracker.Clear();
        }

        return new BulkStartTransportFromLoadingResult
        {
            CreatedLegIds = createdLegIds,
            Failures = failures
        };
    }

    /// <summary>یک دسته در یک تراکنش: یک قفلِ گروهی، سه کوئری تجمیعی، یک SaveChanges.</summary>
    private async Task<List<int>> ConvertChunkAsync(
        IReadOnlyList<BulkStartTransportFromLoadingRow> chunk,
        DateTime transportDate,
        string? formToken,
        CancellationToken ct)
    {
        // ترتیب صعودیِ شناسه برای قفل‌گرفتن، تا دو درخواست هم‌زمان روی دو دسته قفل‌ها را
        // در جهت مخالف نگیرند.
        var idArray = chunk.Select(r => r.LoadingRegisterId).Distinct().OrderBy(id => id).ToArray();

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        try
        {
            List<LoadingRegister> lockedLoadings;
            if (_db.Database.IsRelational()
                && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                lockedLoadings = await _db.LoadingRegisters
                    .FromSqlInterpolated(
                        $@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = ANY({idArray}) ORDER BY ""Id"" FOR UPDATE")
                    .ToListAsync(ct);
            }
            else
            {
                lockedLoadings = await _db.LoadingRegisters
                    .Where(l => idArray.Contains(l.Id))
                    .OrderBy(l => l.Id)
                    .ToListAsync(ct);
            }

            var loadings = lockedLoadings.ToDictionary(l => l.Id);
            var received = await ReceivedByLoadingAsync(idArray, ct);
            var shortage = await ShortageByLoadingAsync(idArray, ct);
            var transported = await TransportedByLoadingAsync(idArray, ct);

            // مصرفِ همین دسته: دو ردیف می‌توانند به یک بارگیری اشاره کنند و نباید با هم
            // بیشتر از مانده بردارند — همان چیزی که در مسیر تکی با خواندن دوبارهٔ مانده رخ می‌داد.
            var consumedInChunk = new Dictionary<int, decimal>();
            var batches = new List<InventoryTransportBatch>(chunk.Count);
            var usageWriter = new AssetUsageChargeService(_db);

            foreach (var row in chunk)
            {
                if (!loadings.TryGetValue(row.LoadingRegisterId, out var loading))
                {
                    throw Rule("TRANSPORT_LOADING_NOT_FOUND", "بارگیری انتخاب‌شده پیدا نشد.");
                }

                var availableMt = AvailableFromLoadingMt(
                    loading.LoadedQuantityMt,
                    received.GetValueOrDefault(loading.Id),
                    shortage.GetValueOrDefault(loading.Id),
                    transported.GetValueOrDefault(loading.Id) + consumedInChunk.GetValueOrDefault(loading.Id));
                if (row.QuantityMt > availableMt + Epsilon)
                {
                    throw InsufficientLoading(availableMt);
                }

                var draft = new LoadingConversionDraft
                {
                    QuantityMt = row.QuantityMt,
                    TransportType = row.TransportType,
                    TruckId = row.TruckId,
                    WagonId = row.WagonId,
                    VesselId = row.VesselId,
                    DriverId = row.DriverId,
                    ServiceProviderId = row.ServiceProviderId,
                    TransportDate = transportDate,
                    Reference = row.Reference,
                    Notes = row.Notes
                };
                // بدون دارایی ملکی (این مسیر هرگز OperationalAssetId نمی‌گذارد) هیچ کوئری‌ای نمی‌زند.
                var carrierParty = await usageWriter.ResolveCarrierPartyAsync(
                    draft.ServiceProviderId,
                    draft.TransportType == LoadingTransportType.Truck ? draft.DriverId : null,
                    operationalAssetId: null,
                    transportDate.Date,
                    ct);
                batches.Add(BuildLoadingConversion(loading, draft, carrierParty));
                consumedInChunk[loading.Id] =
                    consumedInChunk.GetValueOrDefault(loading.Id) + row.QuantityMt;
            }

            _db.InventoryTransportBatches.AddRange(batches);
            _formTokens?.Stamp(formToken, "Transport.BulkFromLoading", nameof(InventoryTransportLeg));
            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return batches.Select(b => b.Legs.Single().Id).ToList();
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    /// <summary>بازگشت به مسیر تکی برای دسته‌ای که شکست خورده — تا فقط ردیفِ مقصر رد شود.</summary>
    private async Task ConvertChunkRowByRowAsync(
        IReadOnlyList<BulkStartTransportFromLoadingRow> chunk,
        DateTime transportDate,
        List<int> createdLegIds,
        List<BulkStartTransportFromLoadingFailure> failures,
        CancellationToken ct)
    {
        foreach (var row in chunk)
        {
            try
            {
                var leg = await StartFromLoadingAsync(new StartTransportFromLoadingCommand
                {
                    LoadingRegisterId = row.LoadingRegisterId,
                    QuantityMt = row.QuantityMt,
                    TransportType = row.TransportType,
                    TruckId = row.TruckId,
                    WagonId = row.WagonId,
                    VesselId = row.VesselId,
                    DriverId = row.DriverId,
                    ServiceProviderId = row.ServiceProviderId,
                    TransportDate = transportDate,
                    Reference = row.Reference,
                    Notes = row.Notes
                }, ct);
                createdLegIds.Add(leg.Id);
            }
            catch (BusinessRuleException ex)
            {
                failures.Add(new BulkStartTransportFromLoadingFailure(
                    row.LoadingRegisterId, row.Label, ex.Code, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && _formTokens?.IsDuplicate(ex) != true)
            {
                // ردیفِ این خطا خودش برگشت خورده (تراکنشِ همان ردیف). اگر اینجا نمی‌گرفتیم،
                // خطای ردیفِ ۳ از هزاران ردیفِ ثبت‌شدهٔ قبلی هم یک 500 می‌ساخت و کاربر
                // فکر می‌کرد هیچ‌چیز ثبت نشده است — در حالی که ثبت شده بود.
                failures.Add(new BulkStartTransportFromLoadingFailure(
                    row.LoadingRegisterId, row.Label, "TRANSPORT_LOADING_ROW_FAILED", ex.Message));
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    private sealed record ActiveVehicleSets(
        HashSet<int> Trucks,
        HashSet<int> Wagons,
        HashSet<int> Vessels,
        HashSet<int> Drivers,
        HashSet<int> ServiceProviders);

    /// <summary>
    /// همان قاعدهٔ اعتبارسنجی وسیلهٔ مسیر تکی، فقط یک‌بار برای کل فرمان: حداکثر ۵ کوئری
    /// به‌جای تا ۳N کوئری.
    /// </summary>
    private async Task<ActiveVehicleSets> LoadActiveVehicleSetsAsync(
        IReadOnlyList<BulkStartTransportFromLoadingRow> rows,
        CancellationToken ct)
    {
        var truckIds = DistinctIds(rows, r => r.TransportType == LoadingTransportType.Truck ? r.TruckId : null);
        var wagonIds = DistinctIds(rows, r => r.TransportType == LoadingTransportType.Wagon ? r.WagonId : null);
        var vesselIds = DistinctIds(rows, r => r.TransportType == LoadingTransportType.Vessel ? r.VesselId : null);
        var driverIds = DistinctIds(rows, r => r.TransportType == LoadingTransportType.Truck ? r.DriverId : null);
        var providerIds = DistinctIds(rows, r => r.ServiceProviderId);

        return new ActiveVehicleSets(
            await ActiveIdsAsync(_db.Trucks.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id), truckIds, ct),
            await ActiveIdsAsync(_db.Wagons.AsNoTracking().Where(w => w.IsActive).Select(w => w.Id), wagonIds, ct),
            await ActiveIdsAsync(_db.Vessels.AsNoTracking().Where(v => v.IsActive).Select(v => v.Id), vesselIds, ct),
            await ActiveIdsAsync(_db.Drivers.AsNoTracking().Where(d => d.IsActive).Select(d => d.Id), driverIds, ct),
            await ActiveIdsAsync(_db.ServiceProviders.AsNoTracking().Where(p => p.IsActive).Select(p => p.Id), providerIds, ct));

        static List<int> DistinctIds(
            IReadOnlyList<BulkStartTransportFromLoadingRow> source,
            Func<BulkStartTransportFromLoadingRow, int?> selector)
            => source.Select(selector).Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();

        static async Task<HashSet<int>> ActiveIdsAsync(
            IQueryable<int> activeIds,
            List<int> wanted,
            CancellationToken token)
            => wanted.Count == 0
                ? []
                : [.. await activeIds.Where(id => wanted.Contains(id)).ToListAsync(token)];
    }

    /// <summary>خطاهای ردیفی — همان کدها و همان پیام‌های مسیر تکی.</summary>
    private static BulkStartTransportFromLoadingFailure? ValidateBulkRow(
        BulkStartTransportFromLoadingRow row,
        ActiveVehicleSets vehicles)
    {
        if (row.LoadingRegisterId <= 0)
        {
            return Fail(row, "TRANSPORT_LOADING_NOT_FOUND", "بارگیری انتخاب‌شده پیدا نشد.");
        }
        if (row.QuantityMt <= 0m)
        {
            return Fail(row, "TRANSPORT_LOADING_QTY_INVALID", "مقدار حمل باید بزرگ‌تر از صفر باشد.");
        }

        var vehicleOk = row.TransportType switch
        {
            LoadingTransportType.Truck => row.TruckId is > 0 && vehicles.Trucks.Contains(row.TruckId.Value),
            LoadingTransportType.Wagon => row.WagonId is > 0 && vehicles.Wagons.Contains(row.WagonId.Value),
            LoadingTransportType.Vessel => row.VesselId is > 0 && vehicles.Vessels.Contains(row.VesselId.Value),
            _ => false
        };
        if (!vehicleOk)
        {
            return Fail(row, "TRANSPORT_RECEIPT_VEHICLE_INVALID", "وسیلهٔ مقصد معتبر و فعال نیست.");
        }

        if (row.TransportType == LoadingTransportType.Truck
            && row.DriverId is > 0
            && !vehicles.Drivers.Contains(row.DriverId.Value))
        {
            return Fail(row, "TRANSPORT_RECEIPT_DRIVER_INVALID", "راننده انتخاب‌شده معتبر و فعال نیست.");
        }
        if (row.ServiceProviderId is > 0 && !vehicles.ServiceProviders.Contains(row.ServiceProviderId.Value))
        {
            return Fail(row, "TRANSPORT_RECEIPT_PROVIDER_INVALID", "شرکت خدماتی انتخاب‌شده معتبر و فعال نیست.");
        }

        return null;

        static BulkStartTransportFromLoadingFailure Fail(
            BulkStartTransportFromLoadingRow row, string code, string message)
            => new(row.LoadingRegisterId, row.Label, code, message);
    }

    private async Task<Dictionary<int, decimal>> ReceivedByLoadingAsync(int[] ids, CancellationToken ct)
        => await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => ids.Contains(r.LoadingRegisterId) && !r.IsCancelled)
            .GroupBy(r => r.LoadingRegisterId)
            .Select(g => new { LoadingId = g.Key, Mt = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(x => x.LoadingId, x => x.Mt, ct);

    private async Task<Dictionary<int, decimal>> TransportedByLoadingAsync(int[] ids, CancellationToken ct)
        => await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId.HasValue
                && ids.Contains(a.SourceLoadingRegisterId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { LoadingId = g.Key, Mt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.LoadingId, x => x.Mt, ct);

    /// <summary>
    /// کسریِ رسید. شرطِ مسیر تکی «یا روی خودِ بارگیری، یا روی رسیدِ آن بارگیری» است و یک رویداد
    /// می‌تواند از هر دو راه به یک بارگیری برسد؛ برای همین هر رویداد با هر دو کلیدش برمی‌گردد و
    /// در حافظه — بدون دوبار شمردنِ یک رویداد برای یک بارگیری — تجمیع می‌شود.
    /// </summary>
    private async Task<Dictionary<int, decimal>> ShortageByLoadingAsync(int[] ids, CancellationToken ct)
    {
        var rows = await _db.LossEvents
            .AsNoTracking()
            .Where(e => e.Stage == LossEventStage.ReceiptShortage
                && !e.IsCancelled
                && ((e.LoadingRegisterId.HasValue && ids.Contains(e.LoadingRegisterId.Value))
                    || (e.LoadingReceiptId.HasValue
                        && e.LoadingReceipt != null
                        && ids.Contains(e.LoadingReceipt.LoadingRegisterId))))
            .Select(e => new
            {
                e.LoadingRegisterId,
                ReceiptLoadingId = e.LoadingReceiptId.HasValue && e.LoadingReceipt != null
                    ? (int?)e.LoadingReceipt.LoadingRegisterId
                    : null,
                Mt = e.DifferenceQuantityMt > 0m
                    ? e.DifferenceQuantityMt
                    : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m
            })
            .ToListAsync(ct);

        var wanted = ids.ToHashSet();
        var totals = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            if (row.LoadingRegisterId is int direct && wanted.Contains(direct))
            {
                totals[direct] = totals.GetValueOrDefault(direct) + row.Mt;
            }
            if (row.ReceiptLoadingId is int viaReceipt
                && viaReceipt != row.LoadingRegisterId
                && wanted.Contains(viaReceipt))
            {
                totals[viaReceipt] = totals.GetValueOrDefault(viaReceipt) + row.Mt;
            }
        }
        return totals;
    }

    // ───────────────────── هستهٔ مشترکِ «بارگیری → حمل» ─────────────────────
    // مسیر تکی و مسیر گروهی هر دو از همین دو تابع استفاده می‌کنند تا «تبدیل بارگیری به حمل»
    // فقط یک تعریف داشته باشد. هیچ کوئری‌ای اینجا زده نمی‌شود؛ ورودی‌ها از قبل خوانده شده‌اند.

    private sealed record LoadingConversionDraft
    {
        public required decimal QuantityMt { get; init; }
        public required LoadingTransportType TransportType { get; init; }
        public int? TruckId { get; init; }
        public int? WagonId { get; init; }
        public int? VesselId { get; init; }
        public int? DriverId { get; init; }
        public int? ServiceProviderId { get; init; }
        public required DateTime TransportDate { get; init; }
        public string? Reference { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>باقیماندهٔ قابل تبدیل یک بارگیری — تنها تعریفِ این فرمول در مسیر نوشتن.</summary>
    private static decimal AvailableFromLoadingMt(
        decimal loadedMt,
        decimal receivedMt,
        decimal shortageMt,
        decimal transportedMt)
        => Math.Max(loadedMt - receivedMt - shortageMt - transportedMt, 0m);

    private static BusinessRuleException InsufficientLoading(decimal availableMt)
        => Rule(
            "TRANSPORT_LOADING_INSUFFICIENT",
            $"مقدار درخواستی از باقیماندهٔ بارگیری ({availableMt:N4} MT) بیشتر است.");

    /// <summary>
    /// سندِ حملِ یک بارگیری: یک Batch با کلید گروهِ یکتا، یک Leg و یک سهمِ منبع به همان بارگیری.
    /// شکلِ خروجی عمداً دست‌نخورده می‌ماند — گزارش‌ها، بستن قرارداد، کالای در راه و لغو همگی
    /// همین شکل را می‌خوانند (هر تبدیل = یک سفر مستقل با کلید گروهِ خودش).
    /// </summary>
    private static InventoryTransportBatch BuildLoadingConversion(
        LoadingRegister loading,
        LoadingConversionDraft draft,
        CarrierPartyRef? carrierParty)
    {
        var groupKey = $"ITG:{Guid.NewGuid():N}";
        var batch = new InventoryTransportBatch
        {
            BatchNumber = $"ITB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant(),
            SourceTerminalId = null,
            SourceStorageTankId = null,
            ProductId = loading.ProductId,
            TotalQuantityMt = draft.QuantityMt,
            TransportDate = draft.TransportDate.Date,
            Status = InventoryTransportBatchStatus.Loaded,
            TransportGroupKey = groupKey,
            Notes = Normalize(draft.Notes)
        };
        var reference = Normalize(draft.Reference) ?? loading.RwbNo ?? loading.BillOfLadingNumber;
        var leg = new InventoryTransportLeg
        {
            InventoryTransportBatch = batch,
            TransportGroupKey = groupKey,
            SourcePurchaseContractId = loading.ContractId,
            ProductId = loading.ProductId,
            SourceTerminalId = null,
            SourceStorageTankId = null,
            TransportType = draft.TransportType,
            TruckId = draft.TransportType == LoadingTransportType.Truck ? draft.TruckId : null,
            WagonId = draft.TransportType == LoadingTransportType.Wagon ? draft.WagonId : null,
            VesselId = draft.TransportType == LoadingTransportType.Vessel ? draft.VesselId : null,
            DriverId = draft.TransportType == LoadingTransportType.Truck ? draft.DriverId : null,
            ServiceProviderId = draft.ServiceProviderId,
            CarrierType = CarrierType.ServiceProvider,
            CarrierPartyType = carrierParty?.PartyType,
            CarrierPartyId = carrierParty?.PartyId,
            LoadedDate = draft.TransportDate.Date,
            QuantityMt = draft.QuantityMt,
            Status = InventoryTransportLegStatus.Loaded,
            RwbNo = reference,
            BillOfLadingNumber = loading.BillOfLadingNumber,
            RouteDescription = loading.RouteDescription,
            PurchaseUnitCostUsd = loading.LoadingPriceUsd,
            Notes = Normalize(draft.Notes)
        };
        leg.Allocations.Add(new InventoryTransportLegAllocation
        {
            SourceLoadingRegisterId = loading.Id,
            SourcePurchaseContractId = loading.ContractId,
            QuantityMt = decimal.Round(draft.QuantityMt, 4, MidpointRounding.AwayFromZero)
        });
        batch.Legs.Add(leg);
        return batch;
    }

    public async Task<InventoryTransportLeg> StartFromReceiptAsync(
        StartTransportFromReceiptCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.QuantityMt <= 0m)
        {
            throw Rule("TRANSPORT_RECEIPT_QTY_INVALID", "مقدار حمل باید بزرگ‌تر از صفر باشد.");
        }
        if (command.TransportDate == default)
        {
            throw Rule("TRANSPORT_RECEIPT_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        await ValidateVehicleAsync(command, ct);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            var receipt = await _db.LoadingReceipts
                .Include(r => r.LoadingRegister)
                .Include(r => r.Allocations)
                .FirstOrDefaultAsync(r => r.Id == command.LoadingReceiptId, ct)
                ?? throw Rule("TRANSPORT_RECEIPT_NOT_FOUND", "رسید/بارگیری انتخاب‌شده پیدا نشد.");

            if (receipt.IsCancelled
                || receipt.LoadingRegister is null
                || receipt.ReceiptDestination is not (LoadingReceiptDestination.DirectDispatch or LoadingReceiptDestination.Mixed))
            {
                throw Rule("TRANSPORT_RECEIPT_NOT_AVAILABLE", "این رسید منبع مستقیمِ قابل حمل نیست.");
            }

            var sourceRows = receipt.Allocations
                .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                    && a.Status != LoadingReceiptAllocationStatus.Cancelled
                    && a.SourcePurchaseContractId.HasValue
                    && a.QuantityMt > 0m)
                .OrderBy(a => a.Id)
                .ToList();
            if (sourceRows.Count == 0)
            {
                throw Rule("TRANSPORT_RECEIPT_SOURCE_MISSING", "برای این رسید سهم منبعِ قابل حمل ثبت نشده است.");
            }

            var used = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => a.SourceLoadingReceiptId == receipt.Id
                    && a.InventoryTransportLeg != null
                    && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                .GroupBy(a => a.SourcePurchaseContractId)
                .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
                .ToDictionaryAsync(x => x.ContractId, x => x.QuantityMt, ct);

            var available = sourceRows
                .GroupBy(a => a.SourcePurchaseContractId!.Value)
                .Select(g => new ReceiptSource(g.Key, Math.Max(g.Sum(a => a.QuantityMt) - used.GetValueOrDefault(g.Key), 0m)))
                .Where(x => x.QuantityMt > Epsilon)
                .OrderBy(x => x.ContractId)
                .ToList();
            var availableTotal = available.Sum(x => x.QuantityMt);
            if (command.QuantityMt > availableTotal + Epsilon)
            {
                throw Rule(
                    "TRANSPORT_RECEIPT_INSUFFICIENT",
                    $"مقدار درخواستی از باقیماندهٔ مستقیم رسید ({availableTotal:N4} MT) بیشتر است.");
            }

            var groupKey = $"ITG:{Guid.NewGuid():N}";
            var batch = new InventoryTransportBatch
            {
                BatchNumber = $"ITB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant(),
                SourceTerminalId = receipt.TerminalId,
                SourceStorageTankId = null,
                ProductId = receipt.LoadingRegister.ProductId,
                TotalQuantityMt = command.QuantityMt,
                TransportDate = command.TransportDate.Date,
                Status = InventoryTransportBatchStatus.Loaded,
                TransportGroupKey = groupKey,
                Notes = Normalize(command.Notes)
            };

            var leg = new InventoryTransportLeg
            {
                InventoryTransportBatch = batch,
                TransportGroupKey = groupKey,
                SourcePurchaseContractId = available[0].ContractId,
                ProductId = receipt.LoadingRegister.ProductId,
                SourceTerminalId = receipt.TerminalId,
                SourceStorageTankId = null,
                TransportType = command.TransportType,
                TruckId = command.TransportType == LoadingTransportType.Truck ? command.TruckId : null,
                WagonId = command.TransportType == LoadingTransportType.Wagon ? command.WagonId : null,
                VesselId = command.TransportType == LoadingTransportType.Vessel ? command.VesselId : null,
                DriverId = command.TransportType == LoadingTransportType.Truck ? command.DriverId : null,
                ServiceProviderId = command.ServiceProviderId,
                CarrierType = CarrierType.ServiceProvider,
                LoadedDate = command.TransportDate.Date,
                QuantityMt = command.QuantityMt,
                Status = InventoryTransportLegStatus.Loaded,
                RwbNo = Normalize(command.Reference),
                Notes = Normalize(command.Notes)
            };
            var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
                leg.ServiceProviderId,
                leg.DriverId,
                leg.OperationalAssetId,
                leg.LoadedDate,
                ct);
            leg.CarrierPartyType = carrierParty?.PartyType;
            leg.CarrierPartyId = carrierParty?.PartyId;

            var remaining = command.QuantityMt;
            foreach (var source in available)
            {
                var quantity = Math.Min(source.QuantityMt, remaining);
                if (quantity <= Epsilon)
                {
                    continue;
                }
                leg.Allocations.Add(new InventoryTransportLegAllocation
                {
                    SourcePurchaseContractId = source.ContractId,
                    SourceLoadingReceiptId = receipt.Id,
                    SourceInventoryMovementId = null,
                    QuantityMt = decimal.Round(quantity, 4, MidpointRounding.AwayFromZero)
                });
                remaining -= quantity;
            }

            batch.Legs.Add(leg);
            _db.InventoryTransportBatches.Add(batch);
            await _db.SaveChangesAsync(ct);
            await new AssetUsageChargeService(_db).SyncOperationAsync(leg, ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return leg;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    public Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default)
        => _chain.ContinueToVehicleAsync(command, ct);

    public Task<InventoryTransportReceipt> ReceiveToInventoryAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CancellationToken ct = default)
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory)
        {
            throw Rule("TRANSPORT_OUTCOME_NOT_INVENTORY", "مقصد این فرمان باید مخزن باشد.");
        }
        return _outcomes.ApplyAsync(model, leg, saleConversion: null);
    }

    public Task<InventoryTransportReceipt> SellQuantityAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion,
        CancellationToken ct = default)
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale)
        {
            throw Rule("TRANSPORT_OUTCOME_NOT_SALE", "مقصد این فرمان باید فروش در مسیر باشد.");
        }
        return _outcomes.ApplyAsync(model, leg, conversion);
    }

    public Task<LossEventWorkflowResult> RecordLossAsync(
        LossEventSubmission submission,
        CancellationToken ct = default)
        => _losses.CreateAsync(submission, ct);

    public async Task<InventoryTransportReceipt> SettleFreightAsync(
        SettleTransportFreightCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SettlementDate == default)
        {
            throw Rule("TRANSPORT_FREIGHT_DATE_REQUIRED", "تاریخ تسویهٔ کرایه الزامی است.");
        }
        if (command.FreightRateUsdPerMt is < 0m || command.FreightCostUsd is < 0m)
        {
            throw Rule("TRANSPORT_FREIGHT_NEGATIVE", "نرخ یا مبلغ کرایه نمی‌تواند منفی باشد.");
        }
        if (!command.FreightRateUsdPerMt.HasValue && !command.FreightCostUsd.HasValue)
        {
            throw Rule("TRANSPORT_FREIGHT_AMOUNT_REQUIRED", "نرخ فی‌تن یا مبلغ کل کرایه را وارد کنید.");
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var leg = await _outcomes.LoadLegAsync(command.TransportLegId, tracking: true)
                ?? throw Rule("TRANSPORT_FREIGHT_LEG_NOT_FOUND", "حمل موردنظر یافت نشد.");
            if (leg.Status is InventoryTransportLegStatus.Draft or InventoryTransportLegStatus.Cancelled)
            {
                throw Rule("TRANSPORT_FREIGHT_LEG_INVALID", "این حمل در وضعیت قابل تسویه نیست.");
            }
            if (leg.IsFreightSettled)
            {
                throw Rule("TRANSPORT_FREIGHT_ALREADY_SETTLED", "کرایهٔ این حمل قبلاً تسویه شده است.");
            }

            // طرفِ کرایه: اگر کاربر راننده انتخاب کرده و حمل شرکت خدماتی/دارایی ملکی ندارد،
            // همان راننده روی حمل ثبت می‌شود — دقیقاً همان کاری که مسیر تسویهٔ تفصیلی
            // (TruckSettlementsController) می‌کند، تا دو قاعدهٔ موازی ساخته نشود.
            if (command.DriverId is > 0
                && !leg.ServiceProviderId.HasValue
                && !leg.OperationalAssetId.HasValue)
            {
                if (!await _db.Drivers.AnyAsync(d => d.Id == command.DriverId.Value && d.IsActive, ct))
                {
                    throw Rule("TRANSPORT_FREIGHT_DRIVER_INVALID", "راننده معتبر یا فعال نیست.");
                }

                leg.DriverId = command.DriverId;
            }

            var model = new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = leg.Id,
                ReceiptDate = command.SettlementDate.Date,
                SettlementOnly = true,
                ShortageQuantityMt = 0m,
                AllowanceMt = 0m,
                FreightRateUsdPerMt = command.FreightRateUsdPerMt,
                FreightCostUsd = command.FreightCostUsd,
                ServiceProviderId = leg.ServiceProviderId,
                OperationalAssetId = leg.OperationalAssetId,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                Notes = Normalize(command.Notes)
            };
            var modelState = new ModelStateDictionary();
            await _outcomes.ValidateAsync(model, leg, modelState, string.Empty);
            if (!modelState.IsValid)
            {
                var message = modelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                    ?? "اطلاعات تسویهٔ کرایه معتبر نیست.";
                throw Rule("TRANSPORT_FREIGHT_INVALID", message);
            }

            var receipt = await _outcomes.ApplyAsync(model, leg, saleConversion: null);
            if (leg.OperationalAssetId.HasValue)
            {
                await _outcomes.RecordOperationalAssetFreightIncomeAsync(
                    leg.OperationalAssetId.Value,
                    receipt.FreightPayableUsd ?? receipt.FreightCostUsd ?? 0m,
                    command.SettlementDate,
                    leg.SourcePurchaseContractId,
                    leg.ShipmentId,
                    leg.Id,
                    truckDispatchId: null,
                    reference: $"TRANSPORT-RECEIPT:{receipt.Id}",
                    ct: ct);
            }
            leg.IsFreightSettled = true;
            leg.FreightSettledDate = command.SettlementDate.Date;
            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return receipt;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    public Task<IReadOnlyList<InventoryTransportLeg>> CancelOrReverseAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default)
        => _chain.CancelVehicleTransferAsync(sourceReceiptIds, ct);

    private async Task ValidateVehicleAsync(StartTransportFromReceiptCommand command, CancellationToken ct)
        => await ValidateVehicleAsync(
            command.TransportType,
            command.TruckId,
            command.WagonId,
            command.VesselId,
            command.DriverId,
            command.ServiceProviderId,
            ct);

    private async Task ValidateVehicleAsync(
        LoadingTransportType transportType,
        int? truckId,
        int? wagonId,
        int? vesselId,
        int? driverId,
        int? serviceProviderId,
        CancellationToken ct)
    {
        switch (transportType)
        {
            case LoadingTransportType.Truck when truckId.HasValue
                && await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == truckId && t.IsActive, ct):
                break;
            case LoadingTransportType.Wagon when wagonId.HasValue
                && await _db.Wagons.AsNoTracking().AnyAsync(w => w.Id == wagonId && w.IsActive, ct):
                break;
            case LoadingTransportType.Vessel when vesselId.HasValue
                && await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == vesselId && v.IsActive, ct):
                break;
            default:
                throw Rule("TRANSPORT_RECEIPT_VEHICLE_INVALID", "وسیلهٔ مقصد معتبر و فعال نیست.");
        }

        if (driverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == driverId && d.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_DRIVER_INVALID", "راننده انتخاب‌شده معتبر و فعال نیست.");
        }
        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId && p.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_PROVIDER_INVALID", "شرکت خدماتی انتخاب‌شده معتبر و فعال نیست.");
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BusinessRuleException Rule(string code, string message) => new(code, message);

    private sealed record ReceiptSource(int ContractId, decimal QuantityMt);
}
