namespace PTGOilSystem.Web.Models.ContractJourney;

/// <summary>
/// اعداد «چرخه قرارداد» تب خلاصه، همان عبارت‌های Details.cshtml روی همان ViewModel. برای قرارداد
/// خرید چندتأمین‌کننده، خلاصهٔ رسمی هر ردیف جدا ساخته و این اعداد جمع می‌شوند؛ هیچ محاسبهٔ
/// مالی تازه‌ای انجام نمی‌شود و موتور مفاد هر ردیف همان موتور قرارداد عادی است.
/// </summary>
public sealed record ContractLifecycleFigures
{
    public decimal ContractQuantityMt { get; init; }
    public decimal ContractValueUsd { get; init; }
    public decimal LoadingQuantityMt { get; init; }
    public decimal RemainingToLoadMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SalesTotalUsd { get; init; }
    public decimal SaleableQuantityMt { get; init; }
    public decimal ExpenseTotalUsd { get; init; }
    public decimal LossQuantityMt { get; init; }
    public decimal LossCostUsd { get; init; }
    public decimal SupplierPayableTotalUsd { get; init; }
    public decimal SupplierPaidNetUsd { get; init; }
    public decimal SupplierRemainingUsd { get; init; }
    public decimal LifecycleProfitUsd { get; init; }
    public decimal RealizedNetProfitUsd { get; init; }

    public decimal? ExpensePerLoadedMt => LoadingQuantityMt > 0m ? ExpenseTotalUsd / LoadingQuantityMt : null;
    public decimal? MarginPercent => SalesTotalUsd > 0m ? LifecycleProfitUsd / SalesTotalUsd * 100m : null;

    public static ContractLifecycleFigures From(ContractJourneyDetailsViewModel model)
    {
        var metrics = model.SummaryMetrics;
        var useMetrics = model.IsInitialSummaryPayload && metrics.HasValues;
        var sold = useMetrics ? metrics.SaleQuantityMt : model.SalesItems.Sum(s => s.QuantityMt);
        var salesTotal = useMetrics ? metrics.SaleTotalUsd : model.SalesItems.Sum(s => s.AmountUsd);
        var loaded = useMetrics ? metrics.LoadingQuantityMt : model.LoadingItems.Sum(l => l.LoadedQuantityMt);
        var loss = useMetrics
            ? metrics.LossQuantityMt
            : model.LossItems.Sum(l => l.DifferenceQuantityMt > 0m ? l.DifferenceQuantityMt : Math.Max(l.ChargeableLossMt, 0m));
        var contractValue = model.HasMixedLoadingPrices && model.LoadingsValueUsd > 0m
            ? model.LoadingsValueUsd
            : model.PricingFinalUnitPriceUsd is > 0m
                ? model.PricingFinalUnitPriceUsd.Value * model.ContractQuantityMt
                : model.MiniPnl.TraceablePurchaseCostUsd;

        return new ContractLifecycleFigures
        {
            ContractQuantityMt = model.ContractQuantityMt,
            ContractValueUsd = contractValue,
            LoadingQuantityMt = loaded,
            RemainingToLoadMt = Math.Max(model.ContractQuantityMt - loaded, 0m),
            SoldQuantityMt = sold,
            SalesTotalUsd = salesTotal,
            SaleableQuantityMt = model.ContractQuantityMt - sold - loss,
            ExpenseTotalUsd = model.MiniPnl.RecordedExpensesUsd,
            LossQuantityMt = loss,
            LossCostUsd = model.MiniPnl.LossCostUsd,
            SupplierPayableTotalUsd = model.SupplierPayableTotalUsd,
            SupplierPaidNetUsd = model.SupplierPaidNetUsd,
            SupplierRemainingUsd = model.SupplierRemainingUsd,
            LifecycleProfitUsd = model.MiniPnl.LifecycleMarginUsd,
            RealizedNetProfitUsd = model.MiniPnl.RealizedNetProfitUsd
        };
    }

    public static ContractLifecycleFigures Sum(IEnumerable<ContractLifecycleFigures> items)
    {
        var list = items.ToList();
        return new ContractLifecycleFigures
        {
            ContractQuantityMt = list.Sum(x => x.ContractQuantityMt),
            ContractValueUsd = list.Sum(x => x.ContractValueUsd),
            LoadingQuantityMt = list.Sum(x => x.LoadingQuantityMt),
            RemainingToLoadMt = list.Sum(x => x.RemainingToLoadMt),
            SoldQuantityMt = list.Sum(x => x.SoldQuantityMt),
            SalesTotalUsd = list.Sum(x => x.SalesTotalUsd),
            SaleableQuantityMt = list.Sum(x => x.SaleableQuantityMt),
            ExpenseTotalUsd = list.Sum(x => x.ExpenseTotalUsd),
            LossQuantityMt = list.Sum(x => x.LossQuantityMt),
            LossCostUsd = list.Sum(x => x.LossCostUsd),
            SupplierPayableTotalUsd = list.Sum(x => x.SupplierPayableTotalUsd),
            SupplierPaidNetUsd = list.Sum(x => x.SupplierPaidNetUsd),
            SupplierRemainingUsd = list.Sum(x => x.SupplierRemainingUsd),
            LifecycleProfitUsd = list.Sum(x => x.LifecycleProfitUsd),
            RealizedNetProfitUsd = list.Sum(x => x.RealizedNetProfitUsd)
        };
    }
}

/// <summary>خلاصهٔ چرخهٔ یک ردیف تأمین‌کننده در صفحهٔ قرارداد مادر.</summary>
public sealed record PurchaseGroupLineLifecycle(int ContractId, ContractLifecycleFigures Figures);
