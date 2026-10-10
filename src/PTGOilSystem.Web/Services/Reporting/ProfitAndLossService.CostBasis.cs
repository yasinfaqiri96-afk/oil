using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// هزینهٔ مستقیمِ بی‌سندِ یک بارگیری، جدا به «بخشی که بهای کالاست» و «بخشی که مصرف دوره است».
/// </summary>
/// <param name="CapitalizedUsd">
/// کرایهٔ حمل و خط‌آهن (رساندن همین بار به محل فعلی) که فقط روی بارگیری ثبت شده و هیچ سند مصرفی ندارد.
/// </param>
/// <param name="PeriodExpenseUsd">گدام و سایرِ ثبت‌شده روی بارگیری، باز هم بدون سند مصرف.</param>
public sealed record LoadingDirectCostSnapshot(
    int LoadingRegisterId,
    int ContractId,
    DateTime LoadingDate,
    decimal CapitalizedUsd,
    decimal PeriodExpenseUsd);

/// <summary>بهای واحد یک قرارداد خرید تا یک تاریخ.</summary>
public sealed record ContractCostBasisSnapshot(
    int ContractId,
    decimal? WeightedAveragePurchasePriceUsd,
    decimal PricedLoadedMt,
    decimal TotalLoadedMt,
    decimal CapitalizedDirectCostUsd)
{
    /// <summary>کرایهٔ مستقیم بر هر تن بارگیری‌شده (همهٔ تن‌های بارگیری‌شده بار آن را می‌برند).</summary>
    public decimal DirectCostPerMtUsd => TotalLoadedMt > 0m
        ? decimal.Round(CapitalizedDirectCostUsd / TotalLoadedMt, 4, MidpointRounding.AwayFromZero)
        : 0m;

    /// <summary>
    /// بهای واحد موجودی و بهای فروش. بدون قیمت خرید معتبر null است و هیچ عددی ساخته نمی‌شود.
    /// </summary>
    public decimal? UnitCostUsd => WeightedAveragePurchasePriceUsd is > 0m
        ? WeightedAveragePurchasePriceUsd.Value + DirectCostPerMtUsd
        : null;
}

public sealed record PurchaseCostBasis(
    IReadOnlyDictionary<int, ContractCostBasisSnapshot> Contracts,
    IReadOnlyList<LoadingDirectCostSnapshot> Loadings)
{
    public static PurchaseCostBasis Empty { get; } = new(new Dictionary<int, ContractCostBasisSnapshot>(), []);
}

public sealed partial class ProfitAndLossService
{
    // کد نوع مصرفی که LoadingController مصرف خودکار رانندهٔ بارگیری را با آن ثبت و در فیلدهای
    // درون‌خطی آینه می‌کند (LoadingController.Loading*ExpenseCode).
    private const string LoadingTransportExpenseCode = "LOAD-TRANSPORT";
    private const string LoadingStorageExpenseCode = "LOAD-STORAGE";
    private const string LoadingWagonRentExpenseCode = "LOAD-WAGON-RENT";
    private const string LoadingOtherExpenseCode = "LOAD-OTHER";

    /// <summary>
    /// قیمت و هزینهٔ مستقیم فقط از بارگیری‌هایی که تا پایان <paramref name="asOfDate"/> ثبت شده‌اند و لغو
    /// نشده‌اند؛ بنابراین گزارش تاریخی هرگز از بارگیری بعدی قیمت نمی‌گیرد.
    /// <list type="bullet">
    /// <item>میانگین وزنی، مقدار قیمت‌دار و کل مقدار: <see cref="IPurchaseAggregationService.AggregateForLoadedRegisters"/>
    /// — همان حساب و همان قاعدهٔ «قیمت بارگیری، وگرنه قیمت نهایی قرارداد».</item>
    /// <item>هزینهٔ درون‌خطی هر بارگیری: همان تجمیع (که فیلدهای بارگیریِ قدیمیِ دارای سند رسمی را کنار
    /// می‌گذارد و کرایهٔ بدوش فروشنده را نمی‌شمارد)، با قاعدهٔ اجارهٔ رسمی واگن اقتصاد قرارداد.</item>
    /// <item>مصرف خودکار راننده (ExpenseTransaction) که در فیلدهای بارگیریِ ردیف‌دار آینه شده کم می‌شود؛
    /// آن پول سند دارد و در مصارف دوره است.</item>
    /// </list>
    /// </summary>
    public async Task<PurchaseCostBasis> BuildPurchaseCostBasisAsync(
        IReadOnlyCollection<int> purchaseContractIds,
        DateTime asOfDate,
        CancellationToken ct = default)
        => (await BuildPurchaseCostBasesAsync(purchaseContractIds, [asOfDate], ct))[asOfDate.Date];

    /// <summary>
    /// همان <see cref="BuildPurchaseCostBasisAsync"/> برای چند تاریخ با یک بار خواندن داده؛ برای بهای فروش
    /// به تاریخ خودِ هر فروش. کلید نتیجه <c>Date</c> هر تاریخ است و هر تاریخ فقط بارگیری‌های تا پایان همان
    /// روز را می‌بیند.
    /// </summary>
    internal async Task<IReadOnlyDictionary<DateTime, PurchaseCostBasis>> BuildPurchaseCostBasesAsync(
        IReadOnlyCollection<int> purchaseContractIds,
        IReadOnlyCollection<DateTime> asOfDates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseContractIds);
        ArgumentNullException.ThrowIfNull(asOfDates);
        var dates = asOfDates.Select(d => d.Date).Distinct().ToList();
        var ids = purchaseContractIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0 || dates.Count == 0)
        {
            return dates.ToDictionary(d => d, _ => PurchaseCostBasis.Empty);
        }

        // کلید تاریخ تجاری با Kind=Utc، همان قرارداد ستون‌های timestamptz.
        var before = DateTime.SpecifyKind(dates.Max().AddDays(1), DateTimeKind.Utc);
        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .ToListAsync(ct);
        var purchaseIds = contracts.Select(c => c.Id).ToList();
        if (purchaseIds.Count == 0)
        {
            return dates.ToDictionary(d => d, _ => PurchaseCostBasis.Empty);
        }

        var finalPriceById = contracts.ToDictionary(c => c.Id, ContractPricingAdapter.GetCanonicalFinalPrice);
        var registers = await _db.LoadingRegisters.AsNoTracking()
            .Where(l => purchaseIds.Contains(l.ContractId) && !l.IsCancelled && l.LoadingDate < before)
            .Select(l => new LoadingRegister
            {
                Id = l.Id,
                ContractId = l.ContractId,
                LoadingDate = l.LoadingDate,
                LoadedQuantityMt = l.LoadedQuantityMt,
                LoadingPriceUsd = l.LoadingPriceUsd,
                TransportExpenseUsd = l.TransportExpenseUsd,
                WarehouseExpenseUsd = l.WarehouseExpenseUsd,
                OtherExpenseUsd = l.OtherExpenseUsd,
                RailwayExpenseUsd = l.RailwayExpenseUsd,
                FreightCostResponsibility = l.FreightCostResponsibility
            })
            .ToListAsync(ct);
        var registerIds = registers.Select(r => r.Id).ToList();

        // همان دو مجموعه‌ای که PurchaseAggregationService برای کنار گذاشتن آینهٔ اسناد رسمی می‌سازد.
        // ساختاری‌اند، نه تاریخی: فیلد درون‌خطیِ بارگیریِ دارای سند، آینهٔ همان سند است و خودِ سند در
        // تاریخ خودش مصرف دوره می‌شود.
        var documents = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled
                && !e.CustomsDeclarationId.HasValue
                && e.LoadingRegisterId.HasValue
                && registerIds.Contains(e.LoadingRegisterId.Value))
            .Select(e => new
            {
                LoadingRegisterId = e.LoadingRegisterId!.Value,
                IsDriver = e.DriverId.HasValue,
                e.ExpenseBatchId,
                Code = e.ExpenseType != null ? e.ExpenseType.Code : null,
                e.AmountUsd
            })
            .ToListAsync(ct);
        var withOfficialExpenses = documents.Where(d => !d.ExpenseBatchId.HasValue)
            .Select(d => d.LoadingRegisterId).ToHashSet();
        var withExpenseLines = (await _db.LoadingExpenseLines.AsNoTracking()
                .Where(l => registerIds.Contains(l.LoadingRegisterId))
                .Select(l => l.LoadingRegisterId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var driverMirror = documents
            .Where(d => d.IsDriver && d.Code != null)
            .GroupBy(d => (d.LoadingRegisterId, Code: d.Code!.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.AmountUsd));
        decimal Mirrored(int loadingId, string code)
            => withExpenseLines.Contains(loadingId) ? driverMirror.GetValueOrDefault((loadingId, code)) : 0m;

        var (_, _, expenseRows) = await LoadContractExpensesAsync(purchaseIds, purchaseIds, ct);
        var contractsWithOfficialWagonRent = ContractsWithOfficialWagonRent(expenseRows);

        // هزینهٔ مستقیم هر بارگیری فقط به خودِ همان بارگیری بستگی دارد، پس یک بار ساخته می‌شود.
        var allLoadings = new List<LoadingDirectCostSnapshot>(registers.Count);
        foreach (var register in registers)
        {
            var single = _purchaseAggregation.AggregateForLoadedRegisters(
                register.ContractId,
                [register],
                finalPriceById.GetValueOrDefault(register.ContractId),
                withOfficialExpenses,
                withExpenseLines);
            var railway = contractsWithOfficialWagonRent.Contains(register.ContractId)
                ? single.LoadingRailwayExpenseUsdFromLines
                : single.LoadingRailwayExpenseUsd;

            var capitalized = Math.Max(single.LoadingTransportExpenseUsd - Mirrored(register.Id, LoadingTransportExpenseCode), 0m)
                + Math.Max(railway - Mirrored(register.Id, LoadingWagonRentExpenseCode), 0m);
            var periodExpense = Math.Max(single.LoadingWarehouseExpenseUsd - Mirrored(register.Id, LoadingStorageExpenseCode), 0m)
                + Math.Max(single.LoadingOtherExpenseUsd - Mirrored(register.Id, LoadingOtherExpenseCode), 0m);
            if (capitalized != 0m || periodExpense != 0m)
            {
                allLoadings.Add(new LoadingDirectCostSnapshot(
                    register.Id, register.ContractId, register.LoadingDate, capitalized, periodExpense));
            }
        }

        var bases = new Dictionary<DateTime, PurchaseCostBasis>(dates.Count);
        foreach (var date in dates)
        {
            var dateExclusive = date.AddDays(1);
            var loadings = allLoadings.Where(l => l.LoadingDate < dateExclusive).ToList();
            var capitalizedByContract = loadings
                .GroupBy(l => l.ContractId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.CapitalizedUsd));
            var registersByContract = registers
                .Where(r => r.LoadingDate < dateExclusive)
                .GroupBy(r => r.ContractId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new Dictionary<int, ContractCostBasisSnapshot>(purchaseIds.Count);
            foreach (var contractId in purchaseIds)
            {
                var aggregate = _purchaseAggregation.AggregateForLoadedRegisters(
                    contractId,
                    registersByContract.GetValueOrDefault(contractId) ?? [],
                    finalPriceById.GetValueOrDefault(contractId),
                    withOfficialExpenses,
                    withExpenseLines);
                result[contractId] = new ContractCostBasisSnapshot(
                    contractId,
                    aggregate.WeightedAveragePurchasePriceUsd,
                    aggregate.PricedPurchaseQuantityMt,
                    aggregate.TotalLoadedQuantityMt,
                    capitalizedByContract.GetValueOrDefault(contractId));
            }

            bases[date] = new PurchaseCostBasis(result, loadings);
        }

        return bases;
    }
}
