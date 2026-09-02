using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reconciliation;

/// <summary>یک یافتهٔ تطبیق. همیشه شمارش + نمونه، تا هم اندازهٔ مشکل و هم مصداقش معلوم باشد.</summary>
public sealed record LedgerIntegrityFinding(
    string Code,
    string Title,
    int Count,
    IReadOnlyList<string> Samples)
{
    public bool IsClean => Count == 0;
}

public sealed record LedgerIntegrityReport(IReadOnlyList<LedgerIntegrityFinding> Findings)
{
    public bool IsClean => Findings.All(finding => finding.IsClean);

    public int TotalIssues => Findings.Sum(finding => finding.Count);
}

public interface ILedgerIntegrityReconciliationService
{
    Task<LedgerIntegrityReport> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// PTG-P1-02 و ۱۲-F — تشخیصِ فقط‌خواندنیِ سلامتِ دفتر کل و دادهٔ تاریخی.
///
/// چرا FK واقعی ساخته نشد: رابطهٔ <c>(SourceType, SourceId)</c> چندریختی است — یک ستون
/// به ده‌ها جدول اشاره می‌کند. یک FK جعلی یا فقط به یک جدول می‌خورد (بقیه را می‌شکند)
/// یا باید همهٔ اسناد در یک جدول جمع شوند (بازنویسیِ کلِ حسابداری). به‌جای آن، سه لایه:
///
///   ۱) اعتبارسنجیِ لحظهٔ نوشتن — کلیدهای خارجیِ <c>Restrict</c> و مسیرهای کنترل‌شدهٔ ثبت،
///   ۲) قفلِ دوره (PTG-P1-01) که ویرایشِ خارج از مسیر را در گذشته می‌بندد،
///   ۳) همین سرویس: تطبیقِ دوره‌ای که یتیم، گم‌شده و تکراری را پیدا می‌کند.
///
/// <b>محدودیتِ باقی‌مانده، صادقانه:</b> یک <c>DELETE</c>ِ خام روی دیتابیس همچنان می‌تواند
/// ردیفِ یتیم بسازد؛ چیزی جز FK جلویش را نمی‌گیرد و FK برای مدل چندریختی امکان‌پذیر نیست.
/// آنچه تغییر کرده این است که دیگر «بی‌صدا» نمی‌ماند.
///
/// هیچ متدی اینجا چیزی نمی‌نویسد. اجرا روی دادهٔ واقعی بی‌خطر است.
/// </summary>
public sealed class LedgerIntegrityReconciliationService(ApplicationDbContext db)
    : ILedgerIntegrityReconciliationService
{
    private const int SampleSize = 20;

    /// <summary>
    /// SourceTypeهایی که سندِ متناظرشان در جدولِ خودشان است و می‌شود یتیمی را سنجید.
    /// بقیه (تعدیل، مانده اول دوره، تفاوت نرخ و …) عمداً سندِ مستقل ندارند و یتیم شمرده نمی‌شوند.
    /// </summary>
    private static readonly string[] PaymentSourceTypes = Enum.GetNames<PaymentKind>();

    public async Task<LedgerIntegrityReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<LedgerIntegrityFinding>
        {
            await OrphanLedgerRowsAsync(cancellationToken),
            await DocumentsWithoutLedgerAsync(cancellationToken),
            await DuplicateLedgerPostingsAsync(cancellationToken),
            await NegativeClosingInventoryAsync(cancellationToken),
            await InvalidPartnershipSharesAsync(cancellationToken),
            await OverlappingPartnerSharePeriodsAsync(cancellationToken),
            await ContractsOfPartnershipTypeWithoutSharesAsync(cancellationToken),
            await MalformedImportKeysAsync(cancellationToken),
            await BrokenSaleCorrectionChainsAsync(cancellationToken),
            await InvalidConcurrencyVersionsAsync(cancellationToken),
            await PurchaseCostPeriodExposureAsync(cancellationToken),
            await StaleCanonicalSearchKeysAsync(cancellationToken),
        };

        return new LedgerIntegrityReport(findings);
    }

    // ------------------------------------------------------------------
    // ۱ — ردیف دفتر کل که سندش دیگر نیست
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> OrphanLedgerRowsAsync(CancellationToken cancellationToken)
    {
        var samples = new List<string>();
        var count = 0;

        async Task ScanAsync(string sourceType, IQueryable<int> existingIds)
        {
            var orphans = await db.LedgerEntries
                .AsNoTracking()
                .Where(entry => entry.SourceType == sourceType && !existingIds.Contains(entry.SourceId))
                .Select(entry => new { entry.Id, entry.SourceId, entry.EntryDate, entry.AmountUsd })
                .Take(SampleSize)
                .ToListAsync(cancellationToken);

            count += await db.LedgerEntries
                .AsNoTracking()
                .CountAsync(entry => entry.SourceType == sourceType && !existingIds.Contains(entry.SourceId), cancellationToken);

            samples.AddRange(orphans.Select(row =>
                $"{sourceType}#{row.SourceId} → LedgerEntry {row.Id} ({row.EntryDate:yyyy-MM-dd}, {row.AmountUsd:N2} USD)"));
        }

        await ScanAsync("Sale", db.SalesTransactions.Select(x => x.Id));
        await ScanAsync("Expense", db.ExpenseTransactions.Select(x => x.Id));
        await ScanAsync("Loading", db.LoadingRegisters.Select(x => x.Id));
        await ScanAsync("SupplierBalanceTransfer", db.SupplierBalanceTransfers.Select(x => x.Id));
        await ScanAsync("ContractBalanceTransfer", db.ContractBalanceTransfers.Select(x => x.Id));

        var paymentIds = db.PaymentTransactions.Select(x => x.Id);
        foreach (var paymentSourceType in PaymentSourceTypes)
        {
            await ScanAsync(paymentSourceType, paymentIds);
        }

        return new LedgerIntegrityFinding(
            "LEDGER-ORPHAN",
            "ردیف دفتر کل که سند مبدأ آن دیگر وجود ندارد",
            count,
            samples.Take(SampleSize).ToList());
    }

    // ------------------------------------------------------------------
    // ۲ — سندی که اثر مالی دارد ولی ردیف دفتر کل ندارد
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> DocumentsWithoutLedgerAsync(CancellationToken cancellationToken)
    {
        var samples = new List<string>();
        var count = 0;

        var saleLedgerIds = db.LedgerEntries.Where(entry => entry.SourceType == "Sale").Select(entry => entry.SourceId);
        var salesWithoutLedger = await db.SalesTransactions
            .AsNoTracking()
            .Where(sale => !saleLedgerIds.Contains(sale.Id))
            .Select(sale => new { sale.Id, sale.SaleDate, sale.TotalUsd })
            .Take(SampleSize)
            .ToListAsync(cancellationToken);
        count += await db.SalesTransactions.AsNoTracking()
            .CountAsync(sale => !saleLedgerIds.Contains(sale.Id), cancellationToken);
        samples.AddRange(salesWithoutLedger.Select(row => $"Sale#{row.Id} ({row.SaleDate:yyyy-MM-dd}, {row.TotalUsd:N2} USD)"));

        var expenseLedgerIds = db.LedgerEntries.Where(entry => entry.SourceType == "Expense").Select(entry => entry.SourceId);
        var expensesWithoutLedger = await db.ExpenseTransactions
            .AsNoTracking()
            .Where(expense => !expenseLedgerIds.Contains(expense.Id))
            .Select(expense => new { expense.Id, expense.ExpenseDate, expense.AmountUsd })
            .Take(SampleSize)
            .ToListAsync(cancellationToken);
        count += await db.ExpenseTransactions.AsNoTracking()
            .CountAsync(expense => !expenseLedgerIds.Contains(expense.Id), cancellationToken);
        samples.AddRange(expensesWithoutLedger.Select(row => $"Expense#{row.Id} ({row.ExpenseDate:yyyy-MM-dd}, {row.AmountUsd:N2} USD)"));

        return new LedgerIntegrityFinding(
            "LEDGER-MISSING",
            "سند مالی بدون ردیف دفتر کل",
            count,
            samples.Take(SampleSize).ToList());
    }

    // ------------------------------------------------------------------
    // ۳ — یک سند، دو بار در دفتر کل
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> DuplicateLedgerPostingsAsync(CancellationToken cancellationToken)
    {
        // «تکراری» فقط برای مبدأهایی معنی دارد که ذاتاً یک ردیف می‌سازند. پرداخت از طریق
        // صراف عمداً دو ردیف دارد، پس اینجا شمرده نمی‌شود.
        string[] singleRowSourceTypes = ["Sale", "Expense"];

        var duplicates = await db.LedgerEntries
            .AsNoTracking()
            .Where(entry => singleRowSourceTypes.Contains(entry.SourceType))
            .GroupBy(entry => new { entry.SourceType, entry.SourceId })
            .Where(group => group.Count() > 1)
            .Select(group => new { group.Key.SourceType, group.Key.SourceId, Rows = group.Count() })
            .Take(SampleSize)
            .ToListAsync(cancellationToken);

        var count = await db.LedgerEntries
            .AsNoTracking()
            .Where(entry => singleRowSourceTypes.Contains(entry.SourceType))
            .GroupBy(entry => new { entry.SourceType, entry.SourceId })
            .CountAsync(group => group.Count() > 1, cancellationToken);

        return new LedgerIntegrityFinding(
            "LEDGER-DUPLICATE",
            "یک سند با بیش از یک ردیف دفتر کل",
            count,
            duplicates.Select(row => $"{row.SourceType}#{row.SourceId} → {row.Rows} ردیف").ToList());
    }

    // ------------------------------------------------------------------
    // ۴ — موجودی پایانیِ منفی (PTG-P0-02، دادهٔ تاریخی)
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> NegativeClosingInventoryAsync(CancellationToken cancellationToken)
    {
        var negatives = await db.InventoryMovements
            .AsNoTracking()
            .GroupBy(movement => new { movement.ProductId, movement.TerminalId, movement.StorageTankId })
            .Select(group => new
            {
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                Closing = group.Sum(movement => movement.Direction == MovementDirection.In
                    ? movement.QuantityMt
                    : -movement.QuantityMt)
            })
            .Where(scope => scope.Closing < 0m)
            .Take(SampleSize)
            .ToListAsync(cancellationToken);

        return new LedgerIntegrityFinding(
            "INVENTORY-NEGATIVE",
            "موجودی پایانی منفی",
            negatives.Count,
            negatives
                .Select(scope =>
                    $"کالا {scope.ProductId} / ترمینال {scope.TerminalId} / مخزن {scope.StorageTankId?.ToString() ?? "—"}: {scope.Closing:N4} MT")
                .ToList());
    }

    // ------------------------------------------------------------------
    // ۵ — سهم شراکت که در یک دوره ۱۰۰٪ نمی‌شود (PTG-P0-04)
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> InvalidPartnershipSharesAsync(CancellationToken cancellationToken)
    {
        var partnershipContractIds = db.Contracts
            .Where(contract => contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(contract => contract.Id);

        var invalid = await db.ContractPartners
            .AsNoTracking()
            .Where(share => partnershipContractIds.Contains(share.ContractId))
            .GroupBy(share => new { share.ContractId, share.EffectiveFrom })
            .Select(group => new
            {
                group.Key.ContractId,
                group.Key.EffectiveFrom,
                Total = group.Sum(share => share.SharePercent)
            })
            .Where(period => period.Total < 99.9999m || period.Total > 100.0001m)
            .Take(SampleSize)
            .ToListAsync(cancellationToken);

        return new LedgerIntegrityFinding(
            "PARTNER-SHARE-SUM",
            "دورهٔ سهم شراکت که جمع آن ۱۰۰٪ نیست",
            invalid.Count,
            invalid
                .Select(period => $"قرارداد {period.ContractId} از {period.EffectiveFrom:yyyy-MM-dd}: {period.Total:N4}%")
                .ToList());
    }

    // ------------------------------------------------------------------
    // ۶ — بازه‌های سهم که روی هم می‌افتند
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> OverlappingPartnerSharePeriodsAsync(CancellationToken cancellationToken)
    {
        // بازه‌ها کم‌اند (یک سطر برای هر شریک در هر تغییر)، پس مقایسهٔ زوجی در حافظه امن است.
        var rows = await db.ContractPartners
            .AsNoTracking()
            .Select(share => new { share.ContractId, share.PartnerId, share.EffectiveFrom, share.EffectiveTo })
            .ToListAsync(cancellationToken);

        var overlaps = new List<string>();
        foreach (var group in rows.GroupBy(share => new { share.ContractId, share.PartnerId }))
        {
            var ordered = group.OrderBy(share => share.EffectiveFrom).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                var previousEnd = previous.EffectiveTo ?? DateTime.MaxValue;
                if (previousEnd >= current.EffectiveFrom)
                {
                    overlaps.Add(
                        $"قرارداد {group.Key.ContractId} / شریک {group.Key.PartnerId}: "
                        + $"بازهٔ {previous.EffectiveFrom:yyyy-MM-dd} تا {previous.EffectiveTo?.ToString("yyyy-MM-dd") ?? "…"} "
                        + $"با بازهٔ {current.EffectiveFrom:yyyy-MM-dd} هم‌پوشانی دارد");
                }
            }
        }

        return new LedgerIntegrityFinding(
            "PARTNER-PERIOD-OVERLAP",
            "بازه‌های سهم شریک که روی هم افتاده‌اند",
            overlaps.Count,
            overlaps.Take(SampleSize).ToList());
    }

    // ------------------------------------------------------------------
    // ۷ — قرارداد شراکتی بدون هیچ بازهٔ سهم (PTG ۱۲-E)
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> ContractsOfPartnershipTypeWithoutSharesAsync(CancellationToken cancellationToken)
    {
        var contractIdsWithShares = db.ContractPartners.Select(share => share.ContractId);

        var contracts = await db.Contracts
            .AsNoTracking()
            .Where(contract => contract.OwnershipType == ContractOwnershipType.Partnership
                && !contractIdsWithShares.Contains(contract.Id))
            .Select(contract => new { contract.Id, contract.ContractNumber })
            .Take(SampleSize)
            .ToListAsync(cancellationToken);

        var count = await db.Contracts
            .AsNoTracking()
            .CountAsync(contract => contract.OwnershipType == ContractOwnershipType.Partnership
                && !contractIdsWithShares.Contains(contract.Id), cancellationToken);

        return new LedgerIntegrityFinding(
            "PARTNERSHIP-WITHOUT-SHARES",
            "قرارداد شراکتی بدون هیچ سطر سهم",
            count,
            contracts.Select(contract => $"قرارداد {contract.ContractNumber} (#{contract.Id})").ToList());
    }

    // ------------------------------------------------------------------
    // ۸ — کلید ایمپورت که با قاعدهٔ canonical امروز نمی‌خواند (PTG-P1-04)
    // ------------------------------------------------------------------

    private async Task<LedgerIntegrityFinding> MalformedImportKeysAsync(CancellationToken cancellationToken)
    {
        // فقط سطرهایی خوانده می‌شوند که اصلاً کلید دارند؛ کلید null معنی «هویت قابل مقایسه
        // ندارد» می‌دهد و خطا نیست.
        var keys = await db.LoadingRegisters
            .AsNoTracking()
            .Where(loading => loading.ImportUniqueKey != null)
            .Select(loading => new { loading.Id, loading.ImportUniqueKey })
            .ToListAsync(cancellationToken);

        var affected = keys
            .Where(row => !string.Equals(
                row.ImportUniqueKey,
                CanonicaliseImportKey(row.ImportUniqueKey!),
                StringComparison.Ordinal))
            .ToList();

        // مهم‌ترین بخش: کلیدهایی که پس از canonical شدن با هم برخورد می‌کنند. اینها را
        // نباید بی‌صدا یکی کرد؛ باید آدم نگاهشان کند.
        var collisions = keys
            .GroupBy(row => CanonicaliseImportKey(row.ImportUniqueKey!), StringComparer.Ordinal)
            .Where(group => group.Select(row => row.ImportUniqueKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();

        var samples = affected
            .Take(SampleSize / 2)
            .Select(row => $"بارگیری #{row.Id}: «{row.ImportUniqueKey}» → «{CanonicaliseImportKey(row.ImportUniqueKey!)}»")
            .ToList();

        samples.AddRange(collisions
            .Take(SampleSize / 2)
            .Select(group => $"برخورد کلید «{group.Key}»: "
                + string.Join(" ، ", group.Select(row => $"#{row.Id}"))));

        return new LedgerIntegrityFinding(
            "IMPORT-KEY-NON-CANONICAL",
            "کلید ایمپورت بارگیری که با یکسان‌سازی امروز فرق می‌کند (و برخوردهای احتمالی)",
            affected.Count + collisions.Count,
            samples);
    }

    // ------------------------------------------------------------------
    // ۹ — زنجیرهٔ اصلاح فروش که نیم‌بند مانده (PTG-P2-03)
    // ------------------------------------------------------------------

    /// <summary>
    /// پیوندِ «سند اصلی ↔ فروشِ جایگزین» باید همیشه دوطرفه و سالم باشد. سه شکلِ خرابی
    /// ممکن است، و هر سه یعنی کسی نمی‌تواند از روی داده بفهمد چه اتفاقی افتاده:
    ///   • سندی که جایگزین دارد ولی خودش ابطال نشده،
    ///   • پیوندی که به سند ناموجود اشاره می‌کند،
    ///   • پیوندی که سرِ دیگرش برنمی‌گردد.
    /// </summary>
    private async Task<LedgerIntegrityFinding> BrokenSaleCorrectionChainsAsync(CancellationToken cancellationToken)
    {
        var links = await db.SalesTransactions
            .AsNoTracking()
            .Where(sale => sale.ReplacementSaleId != null || sale.CorrectedFromSaleId != null)
            .Select(sale => new
            {
                sale.Id,
                sale.IsCancelled,
                sale.ReplacementSaleId,
                sale.CorrectedFromSaleId,
            })
            .ToListAsync(cancellationToken);

        // سرِ دیگرِ پیوند ممکن است خودش هیچ پیوندی نداشته باشد (دقیقاً همان خرابیِ
        // یک‌طرفه)؛ پس باید جداگانه خوانده شود، وگرنه پیام «ناموجود» می‌داد.
        var referencedIds = links
            .SelectMany(x => new[] { x.ReplacementSaleId, x.CorrectedFromSaleId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var referenced = await db.SalesTransactions
            .AsNoTracking()
            .Where(sale => referencedIds.Contains(sale.Id))
            .Select(sale => new
            {
                sale.Id,
                sale.IsCancelled,
                sale.ReplacementSaleId,
                sale.CorrectedFromSaleId,
            })
            .ToListAsync(cancellationToken);

        var byId = links.Concat(referenced)
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var problems = new List<string>();

        foreach (var link in links)
        {
            if (link.ReplacementSaleId is { } replacementId)
            {
                if (!link.IsCancelled)
                {
                    problems.Add($"فروش #{link.Id} جایگزین دارد ولی ابطال نشده است.");
                }

                if (!byId.TryGetValue(replacementId, out var replacement))
                {
                    problems.Add($"فروش #{link.Id} به جایگزینِ ناموجود #{replacementId} اشاره می‌کند.");
                }
                else if (replacement.CorrectedFromSaleId != link.Id)
                {
                    problems.Add($"پیوند یک‌طرفه: #{link.Id} → #{replacementId} ولی برگشتی نیست.");
                }
            }

            if (link.CorrectedFromSaleId is { } originalId
                && byId.TryGetValue(originalId, out var original)
                && original.ReplacementSaleId != link.Id)
            {
                problems.Add($"پیوند یک‌طرفه: #{link.Id} اصلاحِ #{originalId} است ولی آن سند جای دیگری اشاره می‌کند.");
            }
        }

        return new LedgerIntegrityFinding(
            "SALE-CORRECTION-CHAIN",
            "زنجیرهٔ ابطال/جایگزینِ فروش که ناقص یا یک‌طرفه است",
            problems.Count,
            problems.Take(SampleSize).ToList());
    }

    // ------------------------------------------------------------------
    // ۱۰ — نشانهٔ هم‌زمانیِ نامعتبر (PTG-P1-05)
    // ------------------------------------------------------------------

    /// <summary>
    /// نسخهٔ هر سطر باید همیشه ≥ ۱ باشد. صفر یا منفی یعنی سطری از مسیری بیرون از برنامه
    /// نوشته شده و محافظِ Lost Update روی آن سطر بی‌اثر است.
    /// </summary>
    private async Task<LedgerIntegrityFinding> InvalidConcurrencyVersionsAsync(CancellationToken cancellationToken)
    {
        var samples = new List<string>();
        var total = 0;

        async Task CountAsync<TEntity>(IQueryable<TEntity> source, string label)
            where TEntity : class, IVersionedEntity
        {
            var bad = await source.AsNoTracking()
                .Where(row => row.Version < 1)
                .Take(SampleSize)
                .CountAsync(cancellationToken);
            if (bad > 0)
            {
                total += bad;
                samples.Add($"{label}: {bad} سطر با نسخهٔ نامعتبر");
            }
        }

        await CountAsync(db.PaymentTransactions, "پرداخت");
        await CountAsync(db.ExpenseTransactions, "مصرف");
        await CountAsync(db.SalesTransactions, "فروش");
        await CountAsync(db.Contracts, "قرارداد");
        await CountAsync(db.ContractPartners, "سهم شریک");
        await CountAsync(db.TruckDispatches, "دیسپچ");
        await CountAsync(db.LoadingRegisters, "بارگیری");
        await CountAsync(db.LossEvents, "ضایعات");
        await CountAsync(db.InventoryTransportLegs, "حمل داخلی");

        return new LedgerIntegrityFinding(
            "CONCURRENCY-VERSION-INVALID",
            "سطری که نشانهٔ هم‌زمانی‌اش معتبر نیست",
            total,
            samples);
    }

    // ------------------------------------------------------------------
    // ۱۱ — قراردادهایی که محدودیتِ ۱۲-D واقعاً رویشان اثر دارد
    // ------------------------------------------------------------------

    /// <summary>
    /// PTG ۱۲-D — این اسکنر چیزی را «درست نمی‌کند»؛ دامنهٔ یک محدودیتِ شناخته‌شده را
    /// اندازه می‌گیرد.
    ///
    /// سهم مفادِ هر بازهٔ سهم به نسبتِ <b>عایدِ فروشِ همان بازه</b> تقسیم می‌شود. این کار
    /// وقتی دقیقاً درست است که بهای هر تُنِ قرارداد یکسان باشد — که در قراردادِ تک‌قیمتی
    /// همیشه برقرار است. تنها جایی که نتیجه می‌تواند فرق کند، قراردادی است که هم بیش از
    /// یک بازهٔ سهم دارد و هم بارگیری‌هایش بهای واحدِ متفاوت دارند.
    ///
    /// چرا محاسبهٔ دقیق‌تر ممکن نیست: برای نسبت‌دادنِ بهای یک بارگیریِ مشخص به یک فروشِ
    /// مشخص، لایهٔ <c>SaleLotAllocation</c> لازم است که با <c>Lineage:WriteLots=false</c>
    /// در تولید نوشته نمی‌شود. بدون آن، هر تقسیمِ دیگری حدس است.
    /// </summary>
    private async Task<LedgerIntegrityFinding> PurchaseCostPeriodExposureAsync(CancellationToken cancellationToken)
    {
        var multiPeriodContractIds = await db.ContractPartners
            .AsNoTracking()
            .GroupBy(share => share.ContractId)
            .Where(group => group.Select(share => share.EffectiveFrom).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (multiPeriodContractIds.Count == 0)
        {
            return new LedgerIntegrityFinding(
                "PARTNER-PERIOD-COST-BASIS",
                "قرارداد چندبازه‌ای با بهای واحدِ ناهمگون (محدودیت مستند ۱۲-D)",
                0,
                []);
        }

        var loadings = await db.LoadingRegisters
            .AsNoTracking()
            .Where(loading => multiPeriodContractIds.Contains(loading.ContractId)
                && loading.LoadingPriceUsd != null
                && loading.LoadedQuantityMt > 0m)
            .Select(loading => new { loading.ContractId, Price = loading.LoadingPriceUsd!.Value })
            .ToListAsync(cancellationToken);

        var exposed = loadings
            .GroupBy(row => row.ContractId)
            .Where(group => group.Select(row => decimal.Round(row.Price, 4)).Distinct().Count() > 1)
            .ToList();

        return new LedgerIntegrityFinding(
            "PARTNER-PERIOD-COST-BASIS",
            "قرارداد چندبازه‌ای با بهای واحدِ ناهمگون (محدودیت مستند ۱۲-D)",
            exposed.Count,
            exposed
                .Take(SampleSize)
                .Select(group => $"قرارداد {group.Key}: {group.Select(r => decimal.Round(r.Price, 4)).Distinct().Count()} بهای واحد متفاوت")
                .ToList());
    }

    // ------------------------------------------------------------------
    // ۱۲ — سطرهایی که کلیدِ جستجویشان با متنِ نمایشی جور نیست
    // ------------------------------------------------------------------

    /// <summary>
    /// PTG فاز ۷ — «آیا کسی از جستجو غیب شده؟»
    ///
    /// <c>SearchKey</c> هنگام هر ذخیره ساخته می‌شود، پس در حالت سالم همیشه با متنِ نمایشی
    /// جور است. دو حالت آن را بهم می‌زند: سطرهایی که پیش از این تغییر ثبت شده‌اند و هنوز
    /// Backfill نشده‌اند، و هر نوشتنِ خارج از <c>SaveChanges</c> (SQL دستی یا Restore).
    ///
    /// نتیجهٔ عملی برای کاربر: نامِ آن سطر با املای دیگر پیدا نمی‌شود. این اسکنر — مثل
    /// بقیه — فقط می‌شمارد و نمونه می‌دهد؛ چیزی را اصلاح نمی‌کند. راهِ اصلاح، اجرای
    /// <c>/maintenance/backfill-canonical-search-keys</c> است.
    /// </summary>
    private async Task<LedgerIntegrityFinding> StaleCanonicalSearchKeysAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        var samples = new List<string>();

        async Task ScanAsync<TEntity>(IQueryable<TEntity> source, string label)
            where TEntity : BaseEntity, ICanonicalSearchable
        {
            // مقایسه در حافظه انجام می‌شود چون قاعدهٔ canonical در C# است، نه در SQL.
            var rows = await source.AsNoTracking().ToListAsync(cancellationToken);
            var stale = rows
                .Where(row =>
                {
                    var key = AfghanTextNormalizer.NormalizeForSearch(row.BuildSearchSource());
                    var expected = string.IsNullOrWhiteSpace(key) ? null : key;
                    return !string.Equals(row.SearchKey, expected, StringComparison.Ordinal);
                })
                .ToList();

            if (stale.Count == 0)
            {
                return;
            }

            total += stale.Count;
            samples.Add($"{label}: {stale.Count} سطر (نمونه {string.Join(", ", stale.Take(3).Select(r => $"#{r.Id}"))})");
        }

        await ScanAsync(db.Partners, "شریک");
        await ScanAsync(db.Suppliers, "تأمین‌کننده");
        await ScanAsync(db.Customers, "مشتری");
        await ScanAsync(db.Companies, "شرکت");
        await ScanAsync(db.Trucks, "موتر");
        await ScanAsync(db.Wagons, "واگن");
        await ScanAsync(db.Contracts, "قرارداد");
        await ScanAsync(db.LoadingRegisters, "بارگیری");

        return new LedgerIntegrityFinding(
            "CANONICAL-SEARCH-STALE",
            "سطری که کلید جستجوی canonical آن با متن نمایشی جور نیست",
            total,
            samples.Take(SampleSize).ToList());
    }

    /// <summary>
    /// کلید ذخیره‌شده شکلِ <c>contractId|document|transport…</c> دارد؛ فقط بخش‌های متنی
    /// canonical می‌شوند تا مقایسه با کلیدی که امروز ساخته می‌شود معنی بدهد.
    /// </summary>
    private static string CanonicaliseImportKey(string storedKey)
        => string.Join("|", storedKey.Split('|').Select(AfghanTextNormalizer.CanonicalKey));
}
