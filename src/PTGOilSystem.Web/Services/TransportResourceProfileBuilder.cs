using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.MasterData;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// پروفایل فقط‌خواندنیِ وسیله/راننده برای صفحات Details. تنها از رابطه‌های واقعی (FK) و
/// شمارهٔ واگونِ دقیق می‌خواند؛ هیچ محاسبهٔ مالی یا موجودی در اینجا انجام نمی‌شود.
///
/// صفحه‌بندی سمت DB است: هر منبع (جدول) جدا CountAsync می‌شود، سپس فقط کلیدِ
/// (Id، تاریخ) تا انتهای صفحهٔ خواسته‌شده با OrderBy + Take خوانده، ادغام و برش می‌شود و
/// جزئیات فقط برای ردیف‌های همان صفحه واکشی می‌شود. UNION بین جدول‌ها عمداً ساخته نمی‌شود،
/// چون نوع ستون‌ها (طول varchar و دقت numeric) بین جدول‌ها یکی نیست و EF آن را ترجمه نمی‌کند.
///
/// KPIها روی «عملیات فیزیکی» حساب می‌شوند: ثبت‌هایی که با FK قطعی به هم وصل‌اند یک
/// عملیات‌اند (ببینید <see cref="BuildOperationKpisAsync"/>).
/// </summary>
public static class TransportResourceProfileBuilder
{
    private const string NoRoute = "مسیر ثبت نشده";
    private const int PageSize = TransportResourceProfileViewModel.PageSize;

    // کد منبع؛ ترتیب دوم مرتب‌سازی در ادغام صفحه‌ها (پس از تاریخ) است.
    private const int SrcDispatch = 1, SrcLoading = 2, SrcLeg = 3, SrcShipment = 4;
    private const int DocTicket = 11, DocDeliveryReceipt = 12, DocLoading = 13, DocLeg = 14, DocAsset = 15,
        DocPayment = 16, DocExpense = 17, DocSettlement = 18, DocLoadingReceipt = 19;

    public static async Task<TransportResourceProfileViewModel> ForTruckAsync(
        ApplicationDbContext db,
        Truck truck,
        string? activeTab,
        int tripsPage = 1,
        int docsPage = 1,
        DateTime? today = null)
    {
        var day = (today ?? AfghanistanBusinessClock.SystemToday).Date;
        var q = new ResourceQueries(db)
        {
            Dispatches = db.TruckDispatches.Where(d => d.TruckId == truck.Id),
            Loadings = db.LoadingRegisters.Where(l => l.TruckId == truck.Id),
            Legs = db.InventoryTransportLegs.Where(l => l.TruckId == truck.Id),
            LegKind = "انتقال موتری",
            DispatchCounterpart = d => d.DriverName is null ? null : $"راننده: {d.DriverName}",
            LegCounterpart = l => l.DriverName is null ? null : $"راننده: {l.DriverName}"
        };

        var assetIds = db.OperationalAssets.Where(a => a.LinkedTruckId == truck.Id).Select(a => a.Id);
        var assetDocs = db.AssetDocuments.AsNoTracking().Where(d => assetIds.Contains(d.OperationalAssetId));
        var receipts = db.DeliveryReceipts.AsNoTracking()
            .Where(r => r.TruckDispatch != null && r.TruckDispatch.TruckId == truck.Id && r.DocumentReference != null);

        var docSources = new List<PagedSource<TransportResourceDocumentItem>>
        {
            TicketDocSource(q),
            new(DocDeliveryReceipt,
                receipts.Select(r => new KeyRow { Id = r.Id, Date = r.ReceiptDate }),
                async ids => (await receipts
                        .Where(r => ids.Contains(r.Id))
                        .Select(r => new { r.Id, r.ReceiptDate, r.DocumentReference, r.ReceivedBy, r.TruckDispatchId })
                        .ToListAsync())
                    .ToDictionary(r => r.Id, r => new TransportResourceDocumentItem
                    {
                        Date = r.ReceiptDate,
                        Type = "رسید تخلیه",
                        Number = r.DocumentReference!,
                        Source = r.ReceivedBy ?? $"حواله موتر #{r.TruckDispatchId}",
                        Controller = "Dispatch",
                        Action = "Details",
                        RouteId = r.TruckDispatchId
                    })),
            LoadingDocSource(q, preferBillOfLading: false),
            LegDocSource(q),
            new(DocAsset,
                assetDocs.Select(d => new KeyRow { Id = d.Id, Date = d.IssueDate ?? d.UploadedAtUtc }),
                async ids => (await assetDocs
                        .Where(d => ids.Contains(d.Id))
                        .Select(d => new { d.Id, d.OperationalAssetId, d.DocumentType, d.DocumentNumber, d.OriginalFileName, d.IssueDate, d.UploadedAtUtc, d.ExpiryDate })
                        .ToListAsync())
                    .ToDictionary(d => d.Id, d => new TransportResourceDocumentItem
                    {
                        Date = d.IssueDate ?? d.UploadedAtUtc,
                        Type = AssetDocumentTypeLabel(d.DocumentType),
                        Number = d.DocumentNumber ?? d.OriginalFileName,
                        Source = "مدرک دارایی وصل‌شده",
                        ExpiryDate = d.ExpiryDate,
                        IsExpired = d.ExpiryDate.HasValue && d.ExpiryDate.Value.Date < day,
                        Controller = "OperationalAssets",
                        Action = "Details",
                        RouteId = d.OperationalAssetId
                    }))
        };

        var assets = await db.OperationalAssets
            .AsNoTracking()
            .Where(a => a.LinkedTruckId == truck.Id)
            .OrderBy(a => a.AssetCode)
            .Select(a => new TransportResourceLinkItem { Id = a.Id, Label = a.AssetCode + " — " + a.Name })
            .ToListAsync();
        var expired = await assetDocs.CountAsync(d => d.ExpiryDate != null && d.ExpiryDate < day);

        return await BuildAsync(q, truck.Id, activeTab, tripsPage, docsPage, docSources, assets, expired);
    }

    public static async Task<TransportResourceProfileViewModel> ForDriverAsync(
        ApplicationDbContext db,
        Driver driver,
        string? activeTab,
        int tripsPage = 1,
        int docsPage = 1,
        DateTime? today = null)
    {
        var day = (today ?? AfghanistanBusinessClock.SystemToday).Date;
        var q = new ResourceQueries(db)
        {
            Dispatches = db.TruckDispatches.Where(d => d.DriverId == driver.Id),
            Legs = db.InventoryTransportLegs.Where(l => l.DriverId == driver.Id),
            LegKind = "انتقال موتری",
            DispatchCounterpart = d => d.TruckPlate is null ? null : $"موتر: {d.TruckPlate}",
            LegCounterpart = l => l.TruckPlate is null ? null : $"موتر: {l.TruckPlate}"
        };

        var payments = db.PaymentTransactions.AsNoTracking().Where(p => p.DriverId == driver.Id && p.Reference != null);
        var expenses = db.ExpenseTransactions.AsNoTracking().Where(e => e.DriverId == driver.Id && !e.IsCancelled);
        var settlements = db.SarrafSettlements.AsNoTracking()
            .Where(s => s.DriverId == driver.Id && s.Status != SarrafSettlementStatus.Cancelled);

        var docSources = new List<PagedSource<TransportResourceDocumentItem>>
        {
            TicketDocSource(q),
            LegDocSource(q),
            new(DocPayment,
                payments.Select(p => new KeyRow { Id = p.Id, Date = p.PaymentDate }),
                async ids => (await payments
                        .Where(p => ids.Contains(p.Id))
                        .Select(p => new { p.Id, p.PaymentDate, p.Reference, p.Description, p.Amount, p.Currency })
                        .ToListAsync())
                    .ToDictionary(p => p.Id, p => new TransportResourceDocumentItem
                    {
                        Date = p.PaymentDate,
                        Type = "سند پرداخت",
                        Number = p.Reference!,
                        Source = p.Description ?? $"{p.Amount:N2} {p.Currency}",
                        Controller = "Payments",
                        Action = "Details",
                        RouteId = p.Id
                    })),
            new(DocExpense,
                expenses.Select(e => new KeyRow { Id = e.Id, Date = e.ExpenseDate }),
                async ids => (await expenses
                        .Where(e => ids.Contains(e.Id))
                        .Select(e => new
                        {
                            e.Id,
                            e.ExpenseDate,
                            TypeName = e.ExpenseType != null ? e.ExpenseType.Name : null,
                            e.Description,
                            e.Amount,
                            e.Currency
                        })
                        .ToListAsync())
                    .ToDictionary(e => e.Id, e => new TransportResourceDocumentItem
                    {
                        Date = e.ExpenseDate,
                        Type = string.IsNullOrWhiteSpace(e.TypeName) ? "سند مصرف" : $"مصرف — {e.TypeName}",
                        Number = $"EXP-{e.Id}",
                        Source = e.Description ?? $"{e.Amount:N2} {e.Currency}",
                        Controller = "Expenses",
                        Action = "Details",
                        RouteId = e.Id
                    })),
            new(DocSettlement,
                settlements.Select(s => new KeyRow { Id = s.Id, Date = s.SettlementDate }),
                async ids => (await settlements
                        .Where(s => ids.Contains(s.Id))
                        .Select(s => new
                        {
                            s.Id,
                            s.SettlementDate,
                            s.ReferenceNumber,
                            s.Description,
                            s.RequestedAmount,
                            s.RequestedCurrency,
                            SarrafName = s.Sarraf != null ? s.Sarraf.Name : null
                        })
                        .ToListAsync())
                    .ToDictionary(s => s.Id, s => new TransportResourceDocumentItem
                    {
                        Date = s.SettlementDate,
                        Type = "تسویهٔ صرافی",
                        Number = s.ReferenceNumber ?? $"SS-{s.Id}",
                        Source = s.SarrafName ?? s.Description ?? $"{s.RequestedAmount:N2} {s.RequestedCurrency}",
                        Controller = "SarrafSettlements",
                        Action = "Details",
                        RouteId = s.Id
                    }))
        };

        var assets = await db.AssetAssignments
            .AsNoTracking()
            .Where(a => a.DriverId == driver.Id && (a.ToDate == null || a.ToDate >= day) && a.OperationalAsset != null)
            .OrderBy(a => a.FromDate)
            .Select(a => new TransportResourceLinkItem
            {
                Id = a.OperationalAssetId,
                Label = a.OperationalAsset!.AssetCode + " — " + a.OperationalAsset.Name + " (" + a.Role + ")"
            })
            .ToListAsync();

        return await BuildAsync(q, driver.Id, activeTab, tripsPage, docsPage, docSources, assets, 0);
    }

    public static async Task<TransportResourceProfileViewModel> ForVesselAsync(
        ApplicationDbContext db,
        Vessel vessel,
        string? activeTab,
        int tripsPage = 1,
        int docsPage = 1)
    {
        var q = new ResourceQueries(db)
        {
            Loadings = db.LoadingRegisters.Where(l => l.VesselId == vessel.Id),
            Legs = db.InventoryTransportLegs.Where(l => l.VesselId == vessel.Id),
            Shipments = db.Shipments.Where(s => s.VesselId == vessel.Id),
            LegKind = "انتقال دریایی"
        };

        // رسیدهای ورودی با زیرپرسشِ Id بارگیری‌ها فیلتر می‌شوند؛ بارگیری‌ها جدا واکشی نمی‌شوند.
        var loadingIds = q.Loadings.Select(l => l.Id);
        var receipts = db.LoadingReceipts.AsNoTracking()
            .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled && r.ReferenceDocument != null);

        var docSources = new List<PagedSource<TransportResourceDocumentItem>>
        {
            LoadingDocSource(q, preferBillOfLading: true),
            new(DocLoadingReceipt,
                receipts.Select(r => new KeyRow { Id = r.Id, Date = r.ReceiptDate }),
                async ids => (await receipts
                        .Where(r => ids.Contains(r.Id))
                        .Select(r => new { r.Id, r.ReceiptDate, r.ReferenceDocument, r.LoadingRegisterId })
                        .ToListAsync())
                    .ToDictionary(r => r.Id, r => new TransportResourceDocumentItem
                    {
                        Date = r.ReceiptDate,
                        Type = "رسید ورودی",
                        Number = r.ReferenceDocument!,
                        Source = $"بارگیری #{r.LoadingRegisterId}",
                        Controller = "LoadingReceipts",
                        Action = "Details",
                        RouteId = r.Id
                    })),
            LegDocSource(q)
        };

        return await BuildAsync(q, vessel.Id, activeTab, tripsPage, docsPage, docSources, [], 0);
    }

    public static async Task<TransportResourceProfileViewModel> ForWagonAsync(
        ApplicationDbContext db,
        Wagon wagon,
        string? activeTab,
        int tripsPage = 1,
        int docsPage = 1)
    {
        // فقط رابطهٔ واقعی: WagonId روی انتقال‌ها، یا تطابق دقیقِ شمارهٔ واگون (بدون Contains/حدس).
        var normalized = wagon.WagonNumber.Trim();
        var q = new ResourceQueries(db)
        {
            Loadings = db.LoadingRegisters.Where(l => l.WagonNumber == normalized),
            Legs = db.InventoryTransportLegs.Where(l => l.WagonId == wagon.Id || l.WagonNumber == normalized),
            LegKind = "انتقال ریلی"
        };

        var docSources = new List<PagedSource<TransportResourceDocumentItem>>
        {
            LoadingDocSource(q, preferBillOfLading: false),
            LegDocSource(q)
        };

        return await BuildAsync(q, wagon.Id, activeTab, tripsPage, docsPage, docSources, [], 0);
    }

    private static async Task<TransportResourceProfileViewModel> BuildAsync(
        ResourceQueries q,
        int selectedId,
        string? activeTab,
        int tripsPage,
        int docsPage,
        IReadOnlyList<PagedSource<TransportResourceDocumentItem>> docSources,
        IReadOnlyList<TransportResourceLinkItem> linkedAssets,
        int expiredDocuments)
    {
        var tab = activeTab?.Trim().ToLowerInvariant();
        // فقط صفحهٔ تبِ فعال جزئیات می‌گیرد؛ هر تب دیگر (حتی account بدون سطر) به سوابق برمی‌گردد.
        var loadDocs = tab == "docs";
        var loadTrips = !loadDocs;

        var trips = await PageAsync(TripSources(q), tripsPage, loadTrips);
        var docs = await PageAsync(docSources, docsPage, loadDocs);
        var kpis = await BuildOperationKpisAsync(q);

        return new TransportResourceProfileViewModel
        {
            SelectedId = selectedId,
            ActiveTab = TransportResourceProfileViewModel.NormalizeTab(activeTab),
            Trips = trips.Items,
            TripTotalCount = trips.Total,
            TripPage = trips.Page,
            TripPageCount = trips.PageCount,
            Documents = docs.Items,
            DocumentTotalCount = docs.Total,
            DocumentPage = docs.Page,
            DocumentPageCount = docs.PageCount,
            ExpiredDocumentCount = expiredDocuments,
            LinkedAssets = linkedAssets,
            OperationCount = kpis.Count,
            InProgressOperationCount = kpis.InProgress,
            OperationQuantityMt = kpis.QuantityMt,
            LastOperationDate = kpis.LastDate,
            CancelledRecordCount = kpis.Cancelled
        };
    }

    // ───────────────────────────── DB-side paging ─────────────────────────────

    private sealed class KeyRow
    {
        public int Id { get; init; }
        public DateTime Date { get; init; }
    }

    private sealed record PagedSource<TItem>(
        int Code,
        IQueryable<KeyRow> Keys,
        Func<List<int>, Task<Dictionary<int, TItem>>> Load);

    private sealed record PageResult<TItem>(int Total, int Page, int PageCount, IReadOnlyList<TItem> Items);

    /// <summary>
    /// صفحه‌بندی سمت DB بر چند منبع: CountAsync هر منبع؛ سپس فقط (Id، تاریخ) تا انتهای صفحه با
    /// OrderByDescending + Take از هر منبع؛ ادغام با ترتیب ثابت (تاریخ↓، کد منبع، Id↓) و برش؛
    /// در پایان جزئیات فقط برای Idهای همان صفحه. هیچ‌گاه کل تاریخچه با جزئیات بار نمی‌شود.
    /// </summary>
    private static async Task<PageResult<TItem>> PageAsync<TItem>(
        IReadOnlyList<PagedSource<TItem>> sources,
        int requestedPage,
        bool loadItems)
    {
        var counts = new int[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            counts[i] = await sources[i].Keys.CountAsync();
        }

        var total = counts.Sum();
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        var page = Math.Clamp(requestedPage, 1, pageCount);
        if (!loadItems || total == 0)
        {
            return new PageResult<TItem>(total, page, pageCount, []);
        }

        var needed = page * PageSize;
        var merged = new List<(int Code, KeyRow Row)>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            var keys = await sources[i].Keys
                .OrderByDescending(k => k.Date)
                .ThenByDescending(k => k.Id)
                .Take(needed)
                .ToListAsync();
            merged.AddRange(keys.Select(k => (sources[i].Code, k)));
        }

        var pageKeys = merged
            .OrderByDescending(k => k.Row.Date)
            .ThenBy(k => k.Code)
            .ThenByDescending(k => k.Row.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        var loaded = new Dictionary<int, Dictionary<int, TItem>>();
        foreach (var source in sources)
        {
            var ids = pageKeys.Where(k => k.Code == source.Code).Select(k => k.Row.Id).ToList();
            if (ids.Count > 0)
            {
                loaded[source.Code] = await source.Load(ids);
            }
        }

        var items = pageKeys
            .Where(k => loaded.TryGetValue(k.Code, out var rows) && rows.ContainsKey(k.Row.Id))
            .Select(k => loaded[k.Code][k.Row.Id])
            .ToList();

        return new PageResult<TItem>(total, page, pageCount, items);
    }

    private sealed class ResourceQueries(ApplicationDbContext db)
    {
        public ApplicationDbContext Db { get; } = db;
        public IQueryable<TruckDispatch>? Dispatches { get; init; }
        public IQueryable<LoadingRegister>? Loadings { get; init; }
        public IQueryable<InventoryTransportLeg>? Legs { get; init; }
        public IQueryable<Shipment>? Shipments { get; init; }
        public string LegKind { get; init; } = "انتقال";
        public Func<DispatchRow, string?> DispatchCounterpart { get; init; } = _ => null;
        public Func<LegRow, string?> LegCounterpart { get; init; } = _ => null;
    }

    private static List<PagedSource<TransportResourceTripItem>> TripSources(ResourceQueries q)
    {
        var sources = new List<PagedSource<TransportResourceTripItem>>();
        if (q.Dispatches is { } dispatches)
        {
            sources.Add(new(SrcDispatch,
                dispatches.AsNoTracking().Select(d => new KeyRow { Id = d.Id, Date = d.DispatchDate }),
                async ids => (await DispatchRowsAsync(dispatches.Where(d => ids.Contains(d.Id))))
                    .ToDictionary(d => d.Id, d => DispatchTrip(d, q.DispatchCounterpart(d)))));
        }

        if (q.Loadings is { } loadings)
        {
            sources.Add(new(SrcLoading,
                loadings.AsNoTracking().Select(l => new KeyRow { Id = l.Id, Date = l.LoadingDate }),
                async ids => (await LoadingRowsAsync(loadings.Where(l => ids.Contains(l.Id))))
                    .ToDictionary(l => l.Id, LoadingTrip)));
        }

        if (q.Legs is { } legs)
        {
            sources.Add(new(SrcLeg,
                legs.AsNoTracking().Select(l => new KeyRow { Id = l.Id, Date = l.LoadedDate }),
                async ids => (await LegRowsAsync(legs.Where(l => ids.Contains(l.Id))))
                    .ToDictionary(l => l.Id, l => LegTrip(l, q.LegKind, q.LegCounterpart(l)))));
        }

        if (q.Shipments is { } shipments)
        {
            // تاریخ مرتب‌سازی هرگز null نیست (CreatedAtUtc پشتوانه است) تا ترتیب DB و ادغام یکی بماند.
            sources.Add(new(SrcShipment,
                shipments.AsNoTracking().Select(s => new KeyRow { Id = s.Id, Date = s.DepartureDate ?? s.ArrivalDate ?? s.CreatedAtUtc }),
                async ids => (await shipments.AsNoTracking()
                        .Where(s => ids.Contains(s.Id))
                        .Select(s => new
                        {
                            s.Id,
                            s.ShipmentCode,
                            s.DepartureDate,
                            s.ArrivalDate,
                            s.QuantityMt,
                            Origin = s.OriginLocation != null ? s.OriginLocation.Name : null,
                            Destination = s.DestinationLocation != null ? s.DestinationLocation.Name : null,
                            Product = s.Contract != null && s.Contract.Product != null ? s.Contract.Product.Name : null
                        })
                        .ToListAsync())
                    .ToDictionary(s => s.Id, s => new TransportResourceTripItem
                    {
                        Date = s.DepartureDate ?? s.ArrivalDate,
                        Kind = "محموله",
                        Title = s.ShipmentCode,
                        Product = s.Product,
                        Status = s.ArrivalDate.HasValue ? "رسیده" : s.DepartureDate.HasValue ? "در مسیر" : "ثبت شده",
                        QuantityMt = s.QuantityMt,
                        Route = BuildRoute(s.Origin, s.Destination),
                        Reference = s.ShipmentCode,
                        IsInProgress = s.DepartureDate.HasValue && !s.ArrivalDate.HasValue,
                        Controller = "ShipmentPnl",
                        Action = "Details",
                        RouteId = s.Id
                    })));
        }

        return sources;
    }

    private static PagedSource<TransportResourceDocumentItem> TicketDocSource(ResourceQueries q)
    {
        var tickets = q.Dispatches!.AsNoTracking().Where(d => d.TicketSerialNumber != null);
        return new(DocTicket,
            tickets.Select(d => new KeyRow { Id = d.Id, Date = d.DispatchDate }),
            async ids => (await tickets
                    .Where(d => ids.Contains(d.Id))
                    .Select(d => new { d.Id, d.DispatchDate, d.TicketSerialNumber })
                    .ToListAsync())
                .ToDictionary(d => d.Id, d => new TransportResourceDocumentItem
                {
                    Date = d.DispatchDate,
                    Type = "تکت / حواله بارگیری",
                    Number = d.TicketSerialNumber!,
                    Source = $"حواله موتر #{d.Id}",
                    Controller = "Dispatch",
                    Action = "Details",
                    RouteId = d.Id
                }));
    }

    private static PagedSource<TransportResourceDocumentItem> LoadingDocSource(ResourceQueries q, bool preferBillOfLading)
    {
        var docs = q.Loadings!.AsNoTracking().Where(l => l.BillOfLadingNumber != null || l.RwbNo != null);
        return new(DocLoading,
            docs.Select(l => new KeyRow { Id = l.Id, Date = l.LoadingDate }),
            async ids => (await docs
                    .Where(l => ids.Contains(l.Id))
                    .Select(l => new { l.Id, l.LoadingDate, l.BillOfLadingNumber, l.RwbNo, l.ConsigneeName, l.DestinationName })
                    .ToListAsync())
                .ToDictionary(l => l.Id, l =>
                {
                    var useBl = l.BillOfLadingNumber != null && (preferBillOfLading || l.RwbNo == null);
                    return new TransportResourceDocumentItem
                    {
                        Date = l.LoadingDate,
                        Type = useBl ? "Bill of Lading" : "RWB",
                        Number = useBl ? l.BillOfLadingNumber! : l.RwbNo!,
                        Source = l.ConsigneeName ?? l.DestinationName ?? $"بارگیری #{l.Id}",
                        Controller = "Loading",
                        Action = "Details",
                        RouteId = l.Id
                    };
                }));
    }

    private static PagedSource<TransportResourceDocumentItem> LegDocSource(ResourceQueries q)
    {
        var docs = q.Legs!.AsNoTracking().Where(l => l.RwbNo != null || l.BillOfLadingNumber != null);
        return new(DocLeg,
            docs.Select(l => new KeyRow { Id = l.Id, Date = l.LoadedDate }),
            async ids => (await docs
                    .Where(l => ids.Contains(l.Id))
                    .Select(l => new { l.Id, l.LoadedDate, l.RwbNo, l.BillOfLadingNumber })
                    .ToListAsync())
                .ToDictionary(l => l.Id, l => new TransportResourceDocumentItem
                {
                    Date = l.LoadedDate,
                    Type = l.RwbNo != null ? "RWB انتقال" : "بارنامه انتقال",
                    Number = l.RwbNo ?? l.BillOfLadingNumber!,
                    Source = $"انتقال #{l.Id}",
                    Controller = "InventoryTransportLegs",
                    Action = "Details",
                    RouteId = l.Id
                }));
    }

    // ───────────────────────────── Operation KPIs ─────────────────────────────

    private sealed record OperationKpis(int Count, int InProgress, decimal QuantityMt, DateTime? LastDate, int Cancelled);

    private sealed record OperationNode(string Key, int Rank, DateTime? Date, decimal QuantityMt, bool InProgress);

    /// <summary>
    /// شمارش عملیات فیزیکی با گروه‌بندی (union-find) روی رابطه‌های FK قطعی — فقط میان
    /// ثبت‌هایی که خودشان به همین وسیله/راننده وصل‌اند و لغو نشده‌اند:
    ///   • انتقال ↔ بارگیری: InventoryTransportLegAllocation.SourceLoadingRegisterId
    ///     (فرم «حمل از بارگیری» وسیلهٔ همان بارگیری را روی انتقال می‌گذارد)؛
    ///   • محموله ↔ بارگیری: ShipmentLoadingAllocation؛
    ///   • انتقال ↔ محموله: InventoryTransportLeg.ShipmentId.
    /// حوالهٔ موتر (TruckDispatch) با هیچ‌کدام یکی نمی‌شود؛ FK مستقیمی به همان حمل ندارد.
    /// مقدار هر عملیات = جمع مقدارِ ثبت‌های «مرجع» آن گروه با اولویت:
    /// بارگیری (مقدار بارشده) › محموله › انتقال؛ پس بار یک حمل دو بار جمع نمی‌شود.
    /// در جریان = هر عضوِ گروه در جریان باشد؛ آخرین تاریخ = بیشینهٔ تاریخ اعضا.
    /// فقط ستون‌های کلیدی (Id، تاریخ، مقدار، وضعیت) خوانده می‌شوند، بدون join و متن.
    /// </summary>
    private static async Task<OperationKpis> BuildOperationKpisAsync(ResourceQueries q)
    {
        var db = q.Db;
        var nodes = new List<OperationNode>();
        var edges = new List<(string A, string B)>();
        var cancelled = 0;

        if (q.Dispatches is { } dispatches)
        {
            cancelled += await dispatches.CountAsync(d => d.Status == DispatchStatus.Cancelled);
            var rows = await dispatches.AsNoTracking()
                .Where(d => d.Status != DispatchStatus.Cancelled)
                .Select(d => new { d.Id, d.DispatchDate, d.LoadedQuantityMt, d.Status })
                .ToListAsync();
            nodes.AddRange(rows.Select(d => new OperationNode(
                $"D{d.Id}", 4, d.DispatchDate, d.LoadedQuantityMt,
                d.Status is DispatchStatus.Loaded or DispatchStatus.InTransit)));
        }

        var loadingKeys = new HashSet<int>();
        if (q.Loadings is { } loadings)
        {
            var rows = await loadings.AsNoTracking()
                .Select(l => new { l.Id, l.LoadingDate, l.LoadedQuantityMt })
                .ToListAsync();
            loadingKeys.UnionWith(rows.Select(l => l.Id));
            nodes.AddRange(rows.Select(l => new OperationNode($"L{l.Id}", 1, l.LoadingDate, l.LoadedQuantityMt, false)));
        }

        var shipmentKeys = new HashSet<int>();
        if (q.Shipments is { } shipments)
        {
            var rows = await shipments.AsNoTracking()
                .Select(s => new { s.Id, s.DepartureDate, s.ArrivalDate, s.QuantityMt })
                .ToListAsync();
            shipmentKeys.UnionWith(rows.Select(s => s.Id));
            nodes.AddRange(rows.Select(s => new OperationNode(
                $"S{s.Id}", 2, s.DepartureDate ?? s.ArrivalDate, s.QuantityMt,
                s.DepartureDate.HasValue && !s.ArrivalDate.HasValue)));

            if (loadingKeys.Count > 0)
            {
                var shipmentIds = shipments.Select(s => s.Id);
                var links = await db.ShipmentLoadingAllocations.AsNoTracking()
                    .Where(a => shipmentIds.Contains(a.ShipmentId))
                    .Select(a => new { a.ShipmentId, a.LoadingRegisterId })
                    .ToListAsync();
                edges.AddRange(links
                    .Where(a => loadingKeys.Contains(a.LoadingRegisterId))
                    .Select(a => ($"S{a.ShipmentId}", $"L{a.LoadingRegisterId}")));
            }
        }

        if (q.Legs is { } legs)
        {
            cancelled += await legs.CountAsync(l => l.Status == InventoryTransportLegStatus.Cancelled);
            var active = legs.Where(l => l.Status != InventoryTransportLegStatus.Cancelled);
            var rows = await active.AsNoTracking()
                .Select(l => new { l.Id, l.LoadedDate, l.QuantityMt, l.Status, l.ShipmentId })
                .ToListAsync();
            nodes.AddRange(rows.Select(l => new OperationNode(
                $"G{l.Id}", 3, l.LoadedDate, l.QuantityMt,
                l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)));
            edges.AddRange(rows
                .Where(l => l.ShipmentId.HasValue && shipmentKeys.Contains(l.ShipmentId.Value))
                .Select(l => ($"G{l.Id}", $"S{l.ShipmentId}")));

            if (loadingKeys.Count > 0)
            {
                var legIds = active.Select(l => l.Id);
                var links = await db.InventoryTransportLegAllocations.AsNoTracking()
                    .Where(a => a.SourceLoadingRegisterId != null && legIds.Contains(a.InventoryTransportLegId))
                    .Select(a => new { a.InventoryTransportLegId, LoadingId = a.SourceLoadingRegisterId!.Value })
                    .ToListAsync();
                edges.AddRange(links
                    .Where(a => loadingKeys.Contains(a.LoadingId))
                    .Select(a => ($"G{a.InventoryTransportLegId}", $"L{a.LoadingId}")));
            }
        }

        var parent = nodes.ToDictionary(n => n.Key, n => n.Key);
        string Find(string key)
        {
            while (parent[key] != key)
            {
                parent[key] = parent[parent[key]];
                key = parent[key];
            }

            return key;
        }

        foreach (var (a, b) in edges)
        {
            if (parent.ContainsKey(a) && parent.ContainsKey(b))
            {
                parent[Find(a)] = Find(b);
            }
        }

        var operations = nodes.GroupBy(n => Find(n.Key)).ToList();
        var quantity = operations.Sum(group =>
        {
            var rank = group.Min(n => n.Rank);
            return group.Where(n => n.Rank == rank).Sum(n => n.QuantityMt);
        });

        return new OperationKpis(
            operations.Count,
            operations.Count(group => group.Any(n => n.InProgress)),
            quantity,
            nodes.Where(n => n.Date.HasValue).Select(n => n.Date).Max(),
            cancelled);
    }

    // ───────────────────────────── Row projections ─────────────────────────────

    private sealed record DispatchRow(
        int Id, DateTime Date, DispatchStatus Status, decimal LoadedMt, decimal? DischargedMt, decimal? ShortageMt,
        bool IsFreightSettled, string? Ticket, string? ContractNumber, string? Product, string? Destination,
        string? DriverName, string? TruckPlate);

    private sealed record LoadingRow(
        int Id, DateTime Date, decimal LoadedMt, string? BillOfLading, string? Rwb, string? ContractNumber,
        string? Product, string? Origin, string? Destination, string? Consignee);

    private sealed record LegRow(
        int Id, DateTime Date, InventoryTransportLegStatus Status, decimal QuantityMt, bool IsFreightSettled,
        string? Rwb, string? BillOfLading, string? Product, string? Source, string? Destination,
        string? DriverName, string? TruckPlate);

    private static Task<List<DispatchRow>> DispatchRowsAsync(IQueryable<TruckDispatch> query)
        => query.AsNoTracking()
            .Select(d => new DispatchRow(
                d.Id,
                d.DispatchDate,
                d.Status,
                d.LoadedQuantityMt,
                d.DischargedQuantityMt,
                d.ShortageMt,
                d.IsFreightSettled,
                d.TicketSerialNumber,
                d.Contract != null ? d.Contract.ContractNumber : null,
                d.Product != null ? d.Product.Name : null,
                d.DestinationLocation != null
                    ? d.DestinationLocation.Name
                    : d.Contract != null && d.Contract.DestinationLocation != null ? d.Contract.DestinationLocation.Name : null,
                d.Driver != null ? d.Driver.FullName : null,
                d.Truck != null ? d.Truck.PlateNumber : null))
            .ToListAsync();

    private static Task<List<LoadingRow>> LoadingRowsAsync(IQueryable<LoadingRegister> query)
        => query.AsNoTracking()
            .Select(l => new LoadingRow(
                l.Id,
                l.LoadingDate,
                l.LoadedQuantityMt,
                l.BillOfLadingNumber,
                l.RwbNo,
                l.Contract != null ? l.Contract.ContractNumber : null,
                l.Product != null ? l.Product.Name : null,
                l.OriginLocation != null ? l.OriginLocation.Name : null,
                l.DestinationName,
                l.ConsigneeName))
            .ToListAsync();

    private static Task<List<LegRow>> LegRowsAsync(IQueryable<InventoryTransportLeg> query)
        => query.AsNoTracking()
            .Select(l => new LegRow(
                l.Id,
                l.LoadedDate,
                l.Status,
                l.QuantityMt,
                l.IsFreightSettled,
                l.RwbNo,
                l.BillOfLadingNumber,
                l.Product != null ? l.Product.Name : null,
                l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                l.DestinationTerminal != null
                    ? l.DestinationTerminal.Name
                    : l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                l.Driver != null ? l.Driver.FullName : null,
                l.Truck != null ? l.Truck.PlateNumber : null))
            .ToListAsync();

    private static TransportResourceTripItem DispatchTrip(DispatchRow d, string? counterpart)
        => new()
        {
            Date = d.Date,
            Kind = "حوالهٔ موتر",
            Title = counterpart ?? $"حواله #{d.Id}",
            Product = d.Product,
            Status = WithSettlement(DispatchStatusLabel(d.Status), d.IsFreightSettled),
            QuantityMt = d.LoadedMt,
            DeliveredQuantityMt = d.DischargedMt,
            ShortageMt = d.ShortageMt,
            Route = d.Destination ?? NoRoute,
            Reference = d.Ticket ?? d.ContractNumber ?? $"DISP-{d.Id}",
            IsCancelled = d.Status == DispatchStatus.Cancelled,
            IsInProgress = d.Status is DispatchStatus.Loaded or DispatchStatus.InTransit,
            Controller = "Dispatch",
            Action = "Details",
            RouteId = d.Id
        };

    private static TransportResourceTripItem LoadingTrip(LoadingRow l)
        => new()
        {
            Date = l.Date,
            Kind = "بارگیری",
            Title = l.Consignee ?? l.ContractNumber ?? $"بارگیری #{l.Id}",
            Product = l.Product,
            Status = "بارگیری ثبت شده",
            QuantityMt = l.LoadedMt,
            Route = BuildRoute(l.Origin, l.Destination),
            Reference = l.BillOfLading ?? l.Rwb ?? l.ContractNumber ?? $"LOAD-{l.Id}",
            Controller = "Loading",
            Action = "Details",
            RouteId = l.Id
        };

    private static TransportResourceTripItem LegTrip(LegRow l, string kind, string? counterpart)
        => new()
        {
            Date = l.Date,
            Kind = kind,
            Title = counterpart ?? $"انتقال #{l.Id}",
            Product = l.Product,
            Status = WithSettlement(TransportLegStatusLabel(l.Status), l.IsFreightSettled),
            QuantityMt = l.QuantityMt,
            Route = BuildRoute(l.Source, l.Destination),
            Reference = l.Rwb ?? l.BillOfLading ?? $"LEG-{l.Id}",
            IsCancelled = l.Status == InventoryTransportLegStatus.Cancelled,
            IsInProgress = l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit,
            Controller = "InventoryTransportLegs",
            Action = "Details",
            RouteId = l.Id
        };

    private static string WithSettlement(string status, bool isFreightSettled)
        => isFreightSettled ? $"{status} · کرایه تسویه‌شده" : status;

    private static string BuildRoute(string? origin, string? destination)
    {
        var from = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();
        var to = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();

        return (from, to) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{from} → {to}",
            ({ Length: > 0 }, _) => from,
            (_, { Length: > 0 }) => to,
            _ => NoRoute
        };
    }

    private static string DispatchStatusLabel(DispatchStatus status)
        => status switch
        {
            DispatchStatus.Loaded => "بارگیری شده",
            DispatchStatus.InTransit => "در حال حمل",
            DispatchStatus.Delivered => "تحویل شده",
            DispatchStatus.Cancelled => "لغو شده",
            _ => status.ToString()
        };

    private static string TransportLegStatusLabel(InventoryTransportLegStatus status)
        => status switch
        {
            InventoryTransportLegStatus.Draft => "پیش نویس",
            InventoryTransportLegStatus.Loaded => "بارگیری شده",
            InventoryTransportLegStatus.InTransit => "در مسیر انتقال",
            InventoryTransportLegStatus.Received => "رسیده",
            InventoryTransportLegStatus.Cancelled => "لغو شده",
            _ => status.ToString()
        };

    private static string AssetDocumentTypeLabel(AssetDocumentType type)
        => type switch
        {
            AssetDocumentType.Insurance => "بیمه",
            AssetDocumentType.Registration => "جواز / ثبت",
            AssetDocumentType.Ownership => "سند مالکیت",
            AssetDocumentType.Inspection => "معاینه فنی",
            AssetDocumentType.Permit => "مجوز",
            _ => "مدرک دیگر"
        };
}
