using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// اقتصادِ یک قرارداد — تنها مرجعِ «سود قرارداد». دو معیارِ جدا با نامِ جدا دارد:
/// <list type="bullet">
/// <item><b>سودِ محققِ قرارداد</b> (<see cref="RealizedNetProfitUsd"/>): فقط بخشِ فروخته‌شده.
/// عاید − بهای کالای فروخته‌شده − سهمِ فروخته‌شدهٔ هزینه‌های عملیاتی ± اثرِ ارزیِ محقق.</item>
/// <item><b>سودِ چرخهٔ کاملِ قرارداد</b> (<see cref="LifecycleMarginUsd"/>): کلِ خرید و کلِ هزینه‌ها،
/// چه فروخته شده باشد چه نه (برآوردِ اقتصادِ کلِ قرارداد).</item>
/// </list>
/// گزارش مفاد قراردادها، پروندهٔ قرارداد، صورت‌حساب شراکت و ثبتِ سهمِ شریک همه از همین می‌خوانند.
/// </summary>
public sealed record ContractEconomicsSnapshot
{
    public int ContractId { get; init; }
    public ContractType ContractType { get; init; }

    // ── فروش ─────────────────────────────────────────────────────────────
    public IReadOnlyList<AttributedContractSale> Sales { get; init; } = [];
    public decimal SoldQuantityMt => Sales.Sum(s => s.QuantityMt);
    public decimal RevenueUsd => Sales.Sum(s => s.AmountUsd);
    public int DirectSaleQuantityMismatchCount { get; init; }

    // ── خرید (فقط قرارداد خرید) ──────────────────────────────────────────
    public decimal TotalLoadedMt { get; init; }
    public decimal PricedLoadedMt { get; init; }
    public decimal PendingLoadedMt { get; init; }
    public int PendingLoadingCount { get; init; }
    public decimal PurchaseValueUsd { get; init; }
    public decimal? WeightedAveragePurchasePriceUsd { get; init; }

    // ── هزینه‌های عملیاتی (مبنای کامل، پیش از تسهیم) ─────────────────────
    public decimal TransportCostUsd { get; init; }
    public decimal WarehouseCostUsd { get; init; }
    public decimal OtherCostUsd { get; init; }
    public decimal RailwayCostUsd { get; init; }
    public decimal CustomsCostUsd { get; init; }
    /// <summary>مصارفِ ثبت‌شدهٔ قرارداد که بر عهدهٔ شرکت است (<see cref="CostResponsibilityPolicy"/>).</summary>
    public decimal GeneralExpenseCostUsd { get; init; }
    /// <summary>مصارفی که بر عهدهٔ طرفِ بیرونی است و از سودِ شرکت کم نمی‌شود (فقط برای شفافیت).</summary>
    public decimal ExternalPartyBorneExpenseUsd { get; init; }
    public decimal LossCostUsd { get; init; }
    public int UnvaluedLossCount { get; init; }
    public decimal PendingSettlementQuantityMt { get; init; }

    /// <summary>
    /// سهمِ این قرارداد از مصارفِ «بدون تگِ» محموله (ShipmentId دارد، نه قرارداد و نه مسیر حمل)؛
    /// به وزنِ مقدارِ ثبت‌شدهٔ قرارداد در محموله، و در نبودِ آن مقدارِ حمل.
    /// </summary>
    public decimal SharedShipmentExpenseUsd { get; init; }

    /// <summary>
    /// ارزشِ سهمِ ضایعاتِ «بدون تگِ» محموله + کسریِ رسیدِ حملِ موجودی که ضایعهٔ ثبت‌شده ندارد؛
    /// به میانگینِ وزنیِ خریدِ همین قرارداد.
    /// </summary>
    public decimal ShipmentLossCostUsd { get; init; }

    /// <summary>بهای تمام‌شدهٔ استخرِ موجودی برای فروش‌های قرارداد فروش (قرارداد فروش مبنای خرید ندارد).</summary>
    public decimal PoolCostOfGoodsSoldUsd { get; init; }
    public int UncostedSaleCount { get; init; }

    // ── ارز (محقق؛ کامل، نه تسهیم‌شده) ──────────────────────────────────
    public ContractRealizedFxSnapshot Fx { get; init; } = ContractRealizedFxSnapshot.Zero;

    public decimal OperationalCostBaseUsd
        => TransportCostUsd + WarehouseCostUsd + OtherCostUsd + RailwayCostUsd
            + CustomsCostUsd + GeneralExpenseCostUsd + LossCostUsd
            + SharedShipmentExpenseUsd + ShipmentLossCostUsd;

    // ── سودِ چرخهٔ کامل ──────────────────────────────────────────────────
    public decimal LifecycleTotalCostUsd => ContractType == ContractType.Purchase
        ? PurchaseValueUsd + OperationalCostBaseUsd + Fx.SupplierShortfallUsd + Fx.LossUsd - Fx.GainUsd
        : RealizedCostOfGoodsSoldUsd + OperationalCostBaseUsd + Fx.SupplierShortfallUsd + Fx.LossUsd - Fx.GainUsd;

    public decimal LifecycleMarginUsd => PnlMath.GrossProfit(RevenueUsd, LifecycleTotalCostUsd);

    // ── سودِ محقق ───────────────────────────────────────────────────────
    /// <summary>
    /// سهمِ فروخته‌شده از بارِ قیمت‌دار (همان قاعدهٔ پروندهٔ قرارداد). قرارداد فروش مبنای خرید ندارد،
    /// پس هزینه‌هایش کامل به فروش‌هایش تعلق دارد.
    /// </summary>
    public decimal SoldShareRatio => ContractType == ContractType.Purchase
        ? PricedLoadedMt > 0m ? Math.Clamp(SoldQuantityMt / PricedLoadedMt, 0m, 1m) : 0m
        : 1m;

    public decimal RealizedCostOfGoodsSoldUsd => ContractType == ContractType.Purchase
        ? WeightedAveragePurchasePriceUsd.HasValue
            ? decimal.Round(SoldQuantityMt * WeightedAveragePurchasePriceUsd.Value, 2, MidpointRounding.AwayFromZero)
            : 0m
        : PoolCostOfGoodsSoldUsd;

    /// <summary>
    /// سهمِ فروخته‌شدهٔ هزینه‌های عملیاتی. تا وقتی هیچ بارِ قیمت‌داری نیست تسهیم ممکن نیست و کلِ هزینه
    /// کم می‌شود (محافظه‌کارانه؛ با وضعیتِ «نیازمند بررسی»).
    /// </summary>
    public decimal RealizedOperationalCostUsd => ContractType == ContractType.Purchase && WeightedAveragePurchasePriceUsd.HasValue
        ? decimal.Round(OperationalCostBaseUsd * SoldShareRatio, 2, MidpointRounding.AwayFromZero)
        : OperationalCostBaseUsd;

    public decimal RealizedGrossMarginUsd => RevenueUsd - RealizedCostOfGoodsSoldUsd - RealizedOperationalCostUsd;

    public decimal RealizedFxNetUsd => Fx.GainUsd - Fx.LossUsd - Fx.SupplierShortfallUsd;

    public decimal RealizedNetProfitUsd => decimal.Round(
        RealizedGrossMarginUsd + RealizedFxNetUsd, 2, MidpointRounding.AwayFromZero);

    public PnlConfidence Confidence => ContractType == ContractType.Purchase
        ? PendingLoadedMt > 0m || PendingLoadingCount > 0 || DirectSaleQuantityMismatchCount > 0 || UnvaluedLossCount > 0
            ? PnlConfidence.NeedsReview
            : PnlConfidence.Estimated
        : UncostedSaleCount > 0 ? PnlConfidence.NeedsReview : PnlConfidence.Verified;
}

public sealed partial class ProfitAndLossService
{
    public async Task<IReadOnlyDictionary<int, ContractEconomicsSnapshot>> BuildContractEconomicsAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contractIds);
        var ids = contractIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, ContractEconomicsSnapshot>();
        }

        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);
        var purchaseIds = contracts.Where(c => c.ContractType == ContractType.Purchase).Select(c => c.Id).ToList();
        var saleIds = contracts.Where(c => c.ContractType != ContractType.Purchase).Select(c => c.Id).ToList();

        var fx = await BuildRealizedFxByContractAsync(ids, ct);
        var (companyBorne, externalBorne, expenseRows) = await LoadContractExpensesAsync(ids, purchaseIds, ct);

        var result = new Dictionary<int, ContractEconomicsSnapshot>();
        if (purchaseIds.Count > 0)
        {
            foreach (var snapshot in await BuildPurchaseEconomicsAsync(
                contracts.Where(c => purchaseIds.Contains(c.Id)).ToList(), fx, expenseRows, externalBorne, ct))
            {
                result[snapshot.ContractId] = snapshot;
            }
        }

        if (saleIds.Count > 0)
        {
            var sales = await _db.SalesTransactions.AsNoTracking()
                .Where(s => !s.IsCancelled && s.ContractId.HasValue && saleIds.Contains(s.ContractId.Value))
                .Select(s => new { s.Id, ContractId = s.ContractId!.Value, s.SaleDate, s.InvoiceNumber, s.QuantityMt, s.TotalUsd })
                .ToListAsync(ct);
            var saleIdList = sales.Select(s => s.Id).ToArray();
            var costBySale = saleIdList.Length == 0
                ? new Dictionary<int, decimal>()
                : await _db.SalesCostConsumptions.AsNoTracking()
                    .Where(c => c.Status == SalesCostConsumptionStatus.Active && saleIdList.Contains(c.SalesTransactionId))
                    .GroupBy(c => c.SalesTransactionId)
                    .Select(g => new { SaleId = g.Key, CostUsd = g.Sum(c => c.CostUsd) })
                    .ToDictionaryAsync(c => c.SaleId, c => c.CostUsd, ct);

            foreach (var contractId in saleIds)
            {
                var contractSales = sales.Where(s => s.ContractId == contractId).ToList();
                result[contractId] = new ContractEconomicsSnapshot
                {
                    ContractId = contractId,
                    ContractType = contracts.Single(c => c.Id == contractId).ContractType,
                    Sales = contractSales
                        .OrderBy(s => s.SaleDate).ThenBy(s => s.Id)
                        .Select(s => new AttributedContractSale(s.Id, s.SaleDate, s.InvoiceNumber, s.QuantityMt, s.TotalUsd, IsProvenShare: true))
                        .ToList(),
                    PoolCostOfGoodsSoldUsd = contractSales.Sum(s => costBySale.GetValueOrDefault(s.Id)),
                    UncostedSaleCount = contractSales.Count(s => !costBySale.ContainsKey(s.Id)),
                    GeneralExpenseCostUsd = companyBorne.GetValueOrDefault(contractId),
                    ExternalPartyBorneExpenseUsd = externalBorne.GetValueOrDefault(contractId),
                    Fx = fx.GetValueOrDefault(contractId, ContractRealizedFxSnapshot.Zero)
                };
            }
        }

        return result;
    }

    /// <summary>
    /// مصارفِ قراردادها، جدا به «بر عهدهٔ شرکت» و «بر عهدهٔ طرف بیرونی».
    /// مالکِ هر مصرف: اول خودِ ContractId؛ مصرفِ بی‌قرارداد از راهِ مسیرِ حمل، بارگیری یا دیسپچ به
    /// قرارداد خرید می‌رسد (همان دامنهٔ پروندهٔ قرارداد). هر مصرف فقط یک مالک دارد، پس بینِ قراردادها
    /// دوباره شمرده نمی‌شود.
    /// </summary>
    private async Task<(Dictionary<int, decimal> CompanyBorne, Dictionary<int, decimal> ExternalBorne, List<ContractExpenseRow> CompanyRows)>
        LoadContractExpensesAsync(IReadOnlyCollection<int> contractIds, IReadOnlyCollection<int> purchaseIds, CancellationToken ct)
    {
        var baseQuery = _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled
                && ((e.ContractId.HasValue && contractIds.Contains(e.ContractId.Value))
                    || (!e.ContractId.HasValue
                        && ((e.TransportLeg != null && purchaseIds.Contains(e.TransportLeg.SourcePurchaseContractId))
                            || (e.LoadingRegister != null && purchaseIds.Contains(e.LoadingRegister.ContractId))
                            || (e.TruckDispatch != null && purchaseIds.Contains(e.TruckDispatch.ContractId))))));

        var companyRows = (await baseQuery
                .Where(CostResponsibilityPolicy.IsCompanyBorne)
                .Select(e => new ContractExpenseRow(
                    e.ContractId
                        ?? (e.TransportLeg != null ? (int?)e.TransportLeg.SourcePurchaseContractId : null)
                        ?? (e.LoadingRegister != null ? (int?)e.LoadingRegister.ContractId : null)
                        ?? (e.TruckDispatch != null ? (int?)e.TruckDispatch.ContractId : null)
                        ?? 0,
                    e.AmountUsd,
                    e.Description,
                    e.ExpenseType != null ? e.ExpenseType.Code : null,
                    e.ExpenseType != null ? e.ExpenseType.Name : null,
                    e.ExpenseType != null ? e.ExpenseType.NamePersian : null,
                    e.CustomsDeclarationId))
                .ToListAsync(ct))
            .Where(r => contractIds.Contains(r.ContractId))
            .ToList();
        // «بر عهدهٔ طرف بیرونی» فقط با قراردادِ ثبت‌شده معنا دارد، پس مالکش همان ContractId است.
        var externalBorne = await baseQuery
            .Where(CostResponsibilityPolicy.IsExternalPartyBorne)
            .GroupBy(e => e.ContractId!.Value)
            .Select(g => new { ContractId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToDictionaryAsync(x => x.ContractId, x => x.AmountUsd, ct);

        var companyBorne = companyRows
            .GroupBy(r => r.ContractId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AmountUsd));
        return (companyBorne, externalBorne, companyRows);
    }

    private async Task<List<ContractEconomicsSnapshot>> BuildPurchaseEconomicsAsync(
        List<Contract> purchaseContracts,
        IReadOnlyDictionary<int, ContractRealizedFxSnapshot> fx,
        List<ContractExpenseRow> expenseRows,
        Dictionary<int, decimal> externalBorne,
        CancellationToken ct)
    {
        var purchaseIds = purchaseContracts.Select(c => c.Id).ToList();
        var finalPriceById = purchaseContracts.ToDictionary(c => c.Id, ContractPricingAdapter.GetCanonicalFinalPrice);
        var aggregation = await _purchaseAggregation.AggregateForContractsAsync(purchaseIds, finalPriceById, ct);
        var sales = await _saleAttribution.LoadPurchaseContractSalesAsync(purchaseIds, ct);

        decimal? EffectiveLoadingPriceUsd(int contractId, decimal? loadingPriceUsd)
            => loadingPriceUsd is > 0m ? loadingPriceUsd : finalPriceById.GetValueOrDefault(contractId);

        // ضایعاتِ قابلِ شارژ. قیمت (همان قاعدهٔ پروندهٔ قرارداد):
        //   ضایعهٔ حمل و تسویهٔ نهاییِ مخزن → میانگینِ وزنیِ خریدِ همین قرارداد (به بارگیری وصل نیستند)؛
        //   بقیه → قیمتِ همان بارگیری، که از خودِ ضایعه یا رسید/حرکت/دیسپچِ آن پیدا می‌شود؛
        //   ضایعه‌ای که هیچ مسیری به بارگیری ندارد ارزش‌گذاری نمی‌شود و فقط شمرده می‌شود (حدس زده نمی‌شود).
        // مالکِ ضایعه: قرارداد خریدِ خودش، وگرنه قراردادِ مسیرِ حمل/بارگیری/رسید/حرکت/دیسپچش (همان دامنهٔ
        // پروندهٔ قرارداد)؛ هر ضایعه یک مالک دارد.
        var lossRows = (await _db.LossEvents.AsNoTracking()
            .Where(le => !le.IsCancelled
                && le.ChargeableLossMt > 0m
                && ((le.ContractId.HasValue && purchaseIds.Contains(le.ContractId.Value))
                    || (le.TransportLeg != null && purchaseIds.Contains(le.TransportLeg.SourcePurchaseContractId))
                    || (le.LoadingRegister != null && purchaseIds.Contains(le.LoadingRegister.ContractId))
                    || (le.LoadingReceipt != null && le.LoadingReceipt.LoadingRegister != null
                        && purchaseIds.Contains(le.LoadingReceipt.LoadingRegister.ContractId))
                    || (le.InventoryMovement != null && le.InventoryMovement.ContractId.HasValue
                        && purchaseIds.Contains(le.InventoryMovement.ContractId.Value))
                    || (le.TruckDispatch != null && purchaseIds.Contains(le.TruckDispatch.ContractId))))
            .Select(le => new
            {
                ContractId = (le.Contract != null && le.Contract.ContractType == ContractType.Purchase ? (int?)le.ContractId : null)
                    ?? (le.TransportLeg != null ? (int?)le.TransportLeg.SourcePurchaseContractId : null)
                    ?? (le.LoadingRegister != null ? (int?)le.LoadingRegister.ContractId : null)
                    ?? (le.LoadingReceipt != null && le.LoadingReceipt.LoadingRegister != null
                        ? (int?)le.LoadingReceipt.LoadingRegister.ContractId
                        : null)
                    ?? (le.InventoryMovement != null ? le.InventoryMovement.ContractId : null)
                    ?? (le.TruckDispatch != null ? (int?)le.TruckDispatch.ContractId : null)
                    ?? 0,
                le.ChargeableLossMt,
                UsesContractAverage = le.TransportLegId.HasValue || le.Stage == LossEventStage.TankFinalSettlement,
                LoadingRegisterId = le.LoadingRegisterId
                    ?? (le.LoadingReceipt != null ? (int?)le.LoadingReceipt.LoadingRegisterId : null)
                    ?? (le.InventoryMovement != null && le.InventoryMovement.LoadingReceipt != null
                        ? (int?)le.InventoryMovement.LoadingReceipt.LoadingRegisterId
                        : null)
                    ?? (le.TruckDispatch != null
                        && le.TruckDispatch.LoadingReceiptAllocation != null
                        && le.TruckDispatch.LoadingReceiptAllocation.LoadingReceipt != null
                            ? (int?)le.TruckDispatch.LoadingReceiptAllocation.LoadingReceipt.LoadingRegisterId
                            : null)
            })
            .ToListAsync(ct))
            .Where(x => purchaseIds.Contains(x.ContractId))
            .ToList();
        var lossRegisterIds = lossRows.Where(x => x.LoadingRegisterId.HasValue).Select(x => x.LoadingRegisterId!.Value).Distinct().ToList();
        var registerPrice = lossRegisterIds.Count == 0
            ? new Dictionary<int, (int ContractId, decimal? PriceUsd)>()
            : await _db.LoadingRegisters.AsNoTracking()
                .Where(lr => lossRegisterIds.Contains(lr.Id))
                .Select(lr => new { lr.Id, lr.ContractId, lr.LoadingPriceUsd })
                .ToDictionaryAsync(lr => lr.Id, lr => (ContractId: lr.ContractId, PriceUsd: lr.LoadingPriceUsd), ct);
        var lossByContract = lossRows
            .Select(x =>
            {
                decimal? price = x.UsesContractAverage
                    ? aggregation.TryGetValue(x.ContractId, out var agg) ? agg.WeightedAveragePurchasePriceUsd : null
                    : x.LoadingRegisterId.HasValue && registerPrice.TryGetValue(x.LoadingRegisterId.Value, out var register)
                        ? EffectiveLoadingPriceUsd(register.ContractId, register.PriceUsd)
                        : null;
                return new { x.ContractId, x.ChargeableLossMt, Price = price };
            })
            .GroupBy(x => x.ContractId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Cost: g.Where(x => x.Price is > 0m)
                        .Sum(x => decimal.Round(x.ChargeableLossMt * x.Price!.Value, 4, MidpointRounding.AwayFromZero)),
                    Unvalued: g.Count(x => x.Price is not > 0m)));

        var pendingTankSettlement = await LoadPendingTankSettlementAsync(purchaseIds, ct);
        var shipmentShares = await LoadShipmentCostSharesAsync(purchaseIds, ct);
        var (customsByContract, countedCustomsIds) = await LoadCustomsByContractAsync(purchaseIds, ct);

        // «مصارف عمومی» همان پولی را که ستون «گمرک» از اظهارنامه شمرده دوباره نمی‌شمارد.
        var generalExpenseByContract = expenseRows
            .Where(e => !e.CustomsDeclarationId.HasValue || !countedCustomsIds.Contains(e.CustomsDeclarationId.Value))
            .GroupBy(e => e.ContractId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.AmountUsd));
        var contractsWithOfficialWagonRent = expenseRows
            .Where(e => ExpenseClassification.IsWagonRent(e.ExpenseTypeCode, e.ExpenseTypeName, e.ExpenseTypeNamePersian, e.Description))
            .Select(e => e.ContractId)
            .ToHashSet();

        return purchaseContracts.Select(c =>
        {
            aggregation.TryGetValue(c.Id, out var agg);
            lossByContract.TryGetValue(c.Id, out var loss);
            var shipment = shipmentShares.GetValueOrDefault(c.Id);
            // سهمِ ضایعاتِ بدون تگِ محموله و کسریِ حمل به بارگیری وصل نیستند؛ مثل ضایعهٔ حمل به
            // میانگینِ وزنیِ خریدِ همین قرارداد. بی‌قیمت → ارزش‌گذاری نمی‌شود و شمرده می‌شود.
            var shipmentLossMt = shipment.SharedLossMt + shipment.TransportShortageMt;
            var averagePriceUsd = agg?.WeightedAveragePurchasePriceUsd;
            var shipmentLossValued = shipmentLossMt > 0m && averagePriceUsd is > 0m;
            return new ContractEconomicsSnapshot
            {
                ContractId = c.Id,
                ContractType = ContractType.Purchase,
                Sales = sales.For(c.Id),
                DirectSaleQuantityMismatchCount = sales.DirectSaleQuantityMismatchByContract.GetValueOrDefault(c.Id),
                TotalLoadedMt = agg?.TotalLoadedQuantityMt ?? 0m,
                PricedLoadedMt = agg?.PricedPurchaseQuantityMt ?? 0m,
                PendingLoadedMt = agg?.PendingPurchaseQuantityMt ?? 0m,
                PendingLoadingCount = agg?.PendingLoadingCount ?? 0,
                PurchaseValueUsd = agg?.TraceablePurchaseCostUsd ?? 0m,
                WeightedAveragePurchasePriceUsd = agg?.WeightedAveragePurchasePriceUsd,
                TransportCostUsd = agg?.LoadingTransportExpenseUsd ?? 0m,
                WarehouseCostUsd = agg?.LoadingWarehouseExpenseUsd ?? 0m,
                OtherCostUsd = agg?.LoadingOtherExpenseUsd ?? 0m,
                // اجارهٔ رسمیِ واگن از راهِ مصرف شمرده می‌شود؛ برای بارگیریِ قدیمی فیلدِ درون‌خطیِ خط‌آهن
                // همان مبلغ است و کنار می‌رود، ولی سهمِ سطرهای «بدون طرف‌حساب» می‌ماند.
                RailwayCostUsd = contractsWithOfficialWagonRent.Contains(c.Id)
                    ? agg?.LoadingRailwayExpenseUsdFromLines ?? 0m
                    : agg?.LoadingRailwayExpenseUsd ?? 0m,
                CustomsCostUsd = customsByContract.GetValueOrDefault(c.Id),
                GeneralExpenseCostUsd = generalExpenseByContract.GetValueOrDefault(c.Id),
                ExternalPartyBorneExpenseUsd = externalBorne.GetValueOrDefault(c.Id),
                LossCostUsd = loss.Cost,
                UnvaluedLossCount = loss.Unvalued + (shipmentLossMt > 0m && !shipmentLossValued ? 1 : 0),
                PendingSettlementQuantityMt = pendingTankSettlement.GetValueOrDefault(c.Id),
                SharedShipmentExpenseUsd = shipment.SharedExpenseUsd,
                ShipmentLossCostUsd = shipmentLossValued
                    ? decimal.Round(shipmentLossMt * averagePriceUsd!.Value, 4, MidpointRounding.AwayFromZero)
                    : 0m,
                Fx = fx.GetValueOrDefault(c.Id, ContractRealizedFxSnapshot.Zero)
            };
        }).ToList();
    }

    /// <summary>
    /// سهمِ هر قرارداد خرید از مصارف و ضایعاتِ «بدون تگِ» محموله‌هایش، و کسریِ رسیدِ حملِ موجودی
    /// که ضایعهٔ ثبت‌شده ندارد (همان قاعدهٔ پروندهٔ قرارداد).
    /// <list type="bullet">
    /// <item>محموله‌های قرارداد: Shipment.ContractId، ShipmentContracts، و محمولهٔ حمل‌هایش.</item>
    /// <item>وزن: مقدارِ ثبت‌شدهٔ قرارداد در محموله؛ در نبودش مقدارِ حمل‌هایش. محمولهٔ تک‌قراردادیِ قدیمی
    /// (فقط Shipment.ContractId) کاملاً مالِ همان قرارداد است.</item>
    /// <item>کسریِ حمل = max(کسریِ آخرین رسیدِ هر حمل − ضایعهٔ ثبت‌شدهٔ همان حمل‌ها، ۰).</item>
    /// </list>
    /// </summary>
    private async Task<Dictionary<int, (decimal SharedExpenseUsd, decimal SharedLossMt, decimal TransportShortageMt)>>
        LoadShipmentCostSharesAsync(List<int> purchaseIds, CancellationToken ct)
    {
        var result = new Dictionary<int, (decimal SharedExpenseUsd, decimal SharedLossMt, decimal TransportShortageMt)>();
        var legs = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => purchaseIds.Contains(l.SourcePurchaseContractId))
            .Select(l => new { l.Id, l.ShipmentId, ContractId = l.SourcePurchaseContractId, l.QuantityMt })
            .ToListAsync(ct);
        var directShipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.ContractId.HasValue && purchaseIds.Contains(s.ContractId.Value))
            .Select(s => new { s.Id, ContractId = s.ContractId!.Value })
            .ToListAsync(ct);
        var shipmentContracts = await _db.ShipmentContracts.AsNoTracking()
            .Where(sc => purchaseIds.Contains(sc.ContractId))
            .Select(sc => new { sc.ShipmentId, sc.ContractId, QuantityMt = sc.QuantityMt ?? 0m })
            .ToListAsync(ct);

        // ── کسریِ حمل ──
        var legIds = legs.Select(l => l.Id).ToList();
        if (legIds.Count > 0)
        {
            var contractByLeg = legs.ToDictionary(l => l.Id, l => l.ContractId);
            var shortageByContract = (await _db.InventoryTransportReceipts.AsNoTracking()
                    .Where(r => !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId))
                    .Select(r => new { r.Id, r.InventoryTransportLegId, r.ReceiptDate, r.ShortageQuantityMt })
                    .ToListAsync(ct))
                .GroupBy(r => r.InventoryTransportLegId)
                .Select(g => g.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id).First())
                .GroupBy(r => contractByLeg[r.InventoryTransportLegId])
                .ToDictionary(g => g.Key, g => g.Sum(r => r.ShortageQuantityMt));
            var recordedLegLossByContract = (await _db.LossEvents.AsNoTracking()
                    .Where(e => !e.IsCancelled && e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value))
                    .Select(e => new { LegId = e.TransportLegId!.Value, e.DifferenceQuantityMt, e.ChargeableLossMt })
                    .ToListAsync(ct))
                .GroupBy(e => contractByLeg[e.LegId])
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(e => e.DifferenceQuantityMt > 0m ? e.DifferenceQuantityMt : Math.Max(e.ChargeableLossMt, 0m)));
            foreach (var (contractId, shortageMt) in shortageByContract)
            {
                var net = Math.Max(shortageMt - recordedLegLossByContract.GetValueOrDefault(contractId), 0m);
                if (net > 0m)
                {
                    result[contractId] = (0m, 0m, net);
                }
            }
        }

        // ── سهمِ مصرف و ضایعهٔ بدون تگِ محموله ──
        var shipmentIds = directShipments.Select(s => s.Id)
            .Concat(shipmentContracts.Select(s => s.ShipmentId))
            .Concat(legs.Where(l => l.ShipmentId.HasValue).Select(l => l.ShipmentId!.Value))
            .Distinct()
            .ToList();
        if (shipmentIds.Count == 0)
        {
            return result;
        }

        var untaggedExpenseByShipment = await _db.ExpenseTransactions.AsNoTracking()
            .Where(e => !e.IsCancelled
                && e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value)
                && !e.TransportLegId.HasValue
                && !e.ContractId.HasValue
                // مصرفی که بارگیری یا دیسپچ دارد مالکِ مشخص دارد و در LoadContractExpensesAsync شمرده شده.
                && !e.LoadingRegisterId.HasValue
                && !e.TruckDispatchId.HasValue)
            .GroupBy(e => e.ShipmentId!.Value)
            .Select(g => new { ShipmentId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToDictionaryAsync(x => x.ShipmentId, x => x.AmountUsd, ct);
        var untaggedLossByShipment = await _db.LossEvents.AsNoTracking()
            .Where(e => !e.IsCancelled
                && e.ShipmentId.HasValue && shipmentIds.Contains(e.ShipmentId.Value)
                && !e.TransportLegId.HasValue
                && !e.LoadingRegisterId.HasValue
                && !e.LoadingReceiptId.HasValue
                && !e.TruckDispatchId.HasValue
                && !e.SalesTransactionId.HasValue
                && !e.ContractId.HasValue
                && e.DifferenceQuantityMt > 0m)
            .GroupBy(e => e.ShipmentId!.Value)
            .Select(g => new { ShipmentId = g.Key, QuantityMt = g.Sum(e => e.DifferenceQuantityMt) })
            .ToDictionaryAsync(x => x.ShipmentId, x => x.QuantityMt, ct);
        if (untaggedExpenseByShipment.Count == 0 && untaggedLossByShipment.Count == 0)
        {
            return result;
        }

        var allContractWeightByShipment = (await _db.ShipmentContracts.AsNoTracking()
                .Where(sc => shipmentIds.Contains(sc.ShipmentId) && sc.QuantityMt.HasValue && sc.QuantityMt.Value > 0m)
                .Select(sc => new { sc.ShipmentId, QuantityMt = sc.QuantityMt!.Value })
                .ToListAsync(ct))
            .GroupBy(x => x.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));
        var allLegWeightByShipment = (await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.ShipmentId.HasValue && shipmentIds.Contains(l.ShipmentId.Value) && l.QuantityMt > 0m)
                .Select(l => new { ShipmentId = l.ShipmentId!.Value, l.QuantityMt })
                .ToListAsync(ct))
            .GroupBy(x => x.ShipmentId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));

        foreach (var contractId in purchaseIds)
        {
            var contractShipmentIds = directShipments.Where(s => s.ContractId == contractId).Select(s => s.Id)
                .Concat(shipmentContracts.Where(s => s.ContractId == contractId).Select(s => s.ShipmentId))
                .Concat(legs.Where(l => l.ContractId == contractId && l.ShipmentId.HasValue).Select(l => l.ShipmentId!.Value))
                .Distinct();
            var directShipmentIds = directShipments.Where(s => s.ContractId == contractId).Select(s => s.Id).ToHashSet();
            decimal expenseUsd = 0m;
            decimal lossMt = 0m;
            foreach (var shipmentId in contractShipmentIds)
            {
                var thisWeight = shipmentContracts
                    .Where(s => s.ContractId == contractId && s.ShipmentId == shipmentId)
                    .Sum(s => s.QuantityMt);
                if (thisWeight <= 0m)
                {
                    thisWeight = legs
                        .Where(l => l.ContractId == contractId && l.ShipmentId == shipmentId && l.QuantityMt > 0m)
                        .Sum(l => l.QuantityMt);
                }

                var allWeight = allContractWeightByShipment.GetValueOrDefault(shipmentId);
                if (allWeight <= 0m)
                {
                    allWeight = allLegWeightByShipment.GetValueOrDefault(shipmentId);
                }
                if (allWeight <= 0m && directShipmentIds.Contains(shipmentId))
                {
                    thisWeight = 1m;
                    allWeight = 1m;
                }
                if (allWeight <= 0m || thisWeight <= 0m)
                {
                    continue;
                }

                var ratio = Math.Min(thisWeight / allWeight, 1m);
                expenseUsd += untaggedExpenseByShipment.GetValueOrDefault(shipmentId) * ratio;
                lossMt += untaggedLossByShipment.GetValueOrDefault(shipmentId) * ratio;
            }

            if (expenseUsd == 0m && lossMt == 0m)
            {
                continue;
            }

            var existing = result.GetValueOrDefault(contractId);
            result[contractId] = (
                decimal.Round(expenseUsd, 4, MidpointRounding.AwayFromZero),
                decimal.Round(lossMt, 4, MidpointRounding.AwayFromZero),
                existing.TransportShortageMt);
        }

        return result;
    }

    /// <summary>
    /// گمرکِ هر قرارداد خرید از اظهارنامه‌ها؛ هر اظهارنامه یک بار (اول مسیرِ حمل، بعد بارگیری —
    /// همان ترتیبِ CustomsDeclarationExpenseSync).
    /// </summary>
    private async Task<(Dictionary<int, decimal> ByContract, HashSet<int> CountedDeclarationIds)> LoadCustomsByContractAsync(
        List<int> purchaseIds,
        CancellationToken ct)
    {
        var byContract = new Dictionary<int, decimal>();
        var counted = new HashSet<int>();
        var lrIdToContract = await _db.LoadingRegisters.AsNoTracking()
            .Where(lr => purchaseIds.Contains(lr.ContractId))
            .Select(lr => new { lr.Id, lr.ContractId })
            .ToDictionaryAsync(x => x.Id, x => x.ContractId, ct);
        var legIdToContract = await _db.InventoryTransportLegs.AsNoTracking()
            .Where(l => purchaseIds.Contains(l.SourcePurchaseContractId))
            .Select(l => new { l.Id, ContractId = l.SourcePurchaseContractId })
            .ToDictionaryAsync(x => x.Id, x => x.ContractId, ct);
        if (lrIdToContract.Count == 0 && legIdToContract.Count == 0)
        {
            return (byContract, counted);
        }

        var lrIds = lrIdToContract.Keys.ToList();
        var legIds = legIdToContract.Keys.ToList();
        var rows = await _db.CustomsDeclarations.AsNoTracking()
            .Where(cd => (cd.LoadingRegisterId.HasValue && lrIds.Contains(cd.LoadingRegisterId.Value))
                || (cd.TransportLegId.HasValue && legIds.Contains(cd.TransportLegId.Value)))
            .Select(cd => new { cd.Id, cd.LoadingRegisterId, cd.TransportLegId, cd.TotalUsd })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            int? contractId = row.TransportLegId.HasValue && legIdToContract.TryGetValue(row.TransportLegId.Value, out var legContract)
                ? legContract
                : row.LoadingRegisterId.HasValue && lrIdToContract.TryGetValue(row.LoadingRegisterId.Value, out var loadingContract)
                    ? loadingContract
                    : null;
            if (contractId is null)
            {
                continue;
            }

            byContract[contractId.Value] = byContract.GetValueOrDefault(contractId.Value) + row.TotalUsd;
            counted.Add(row.Id);
        }

        return (byContract, counted);
    }

    /// <summary>
    /// مقدارِ هنوز در مخزن از رسیدهای «ضایعات بعداً از تسویهٔ مخزن»؛ تا مثبت است سودِ قرارداد موقت است.
    /// </summary>
    private async Task<Dictionary<int, decimal>> LoadPendingTankSettlementAsync(List<int> purchaseIds, CancellationToken ct)
    {
        var result = new Dictionary<int, decimal>();
        var deferredPairs = await _db.LoadingReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.LossMode == ReceiptLossMode.DeferredTankSettlement
                && r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                && r.StorageTankId != null
                && r.LoadingRegister != null
                && purchaseIds.Contains(r.LoadingRegister.ContractId))
            .Select(r => new { ContractId = r.LoadingRegister!.ContractId, StorageTankId = r.StorageTankId!.Value })
            .Distinct()
            .ToListAsync(ct);
        if (deferredPairs.Count == 0)
        {
            return result;
        }

        var tankIds = deferredPairs.Select(p => p.StorageTankId).Distinct().ToList();
        var balances = (await _db.InventoryMovements.AsNoTracking()
                .Where(m => m.StorageTankId != null && tankIds.Contains(m.StorageTankId!.Value))
                .Select(m => new
                {
                    StorageTankId = m.StorageTankId!.Value,
                    EffectiveContractId = m.ContractId
                        ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                            ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                            : null),
                    m.Direction,
                    m.QuantityMt
                })
                .ToListAsync(ct))
            .Where(m => m.EffectiveContractId.HasValue)
            .GroupBy(m => (m.StorageTankId, ContractId: m.EffectiveContractId!.Value))
            .ToDictionary(
                g => g.Key,
                g => g.Sum(m => m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                    ? m.QuantityMt
                    : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                        ? -m.QuantityMt
                        : 0m));

        foreach (var pair in deferredPairs.Select(p => (p.StorageTankId, p.ContractId)).Distinct())
        {
            if (balances.TryGetValue(pair, out var balance) && balance > 0m)
            {
                result[pair.ContractId] = result.GetValueOrDefault(pair.ContractId) + balance;
            }
        }

        return result;
    }

    private sealed record ContractExpenseRow(
        int ContractId,
        decimal AmountUsd,
        string? Description,
        string? ExpenseTypeCode,
        string? ExpenseTypeName,
        string? ExpenseTypeNamePersian,
        int? CustomsDeclarationId);
}
