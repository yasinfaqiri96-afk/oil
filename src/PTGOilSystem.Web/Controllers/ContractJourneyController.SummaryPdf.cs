using System.Globalization;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// PDF تب «خلاصه»: همان اعداد و همان مراحلِ صفحه، فقط در قالب سند رسمی.
// هیچ فرمول مالی تازه‌ای اینجا نیست؛ هر مقدار دقیقاً مثل Views/ContractJourney/Details.cshtml
// از مدل ساخته می‌شود و قالب عدد از همان فرمت مرکزی خروجی‌ها (PdfDesignSystem) می‌آید.
public partial class ContractJourneyController
{
    internal static ContractJourneySummaryPdfModel BuildSummaryPdfModel(
        ContractJourneyDetailsViewModel model,
        bool isEnglish,
        DateTime generatedAt)
    {
        string T(string fa, string en) => isEnglish ? en : fa;
        var unitMt = T("تن", "MT");
        const string UnitUsd = "USD";
        var pending = T("در انتظار نرخ", "Pending price");
        const string Dash = "—";

        // فرمت اعداد همان فرمت بقیهٔ خروجی‌ها: ارقام لاتین، جداکنندهٔ هزارگان، دو/سه رقم اعشار.
        string Num(decimal value, int decimals) => PdfDesignSystem.FormatPdfNumber(value, isEnglish, decimals);
        string Qty(decimal value) => Num(value, 3);
        string Money(decimal value) => Num(value, 2);
        string Percent(decimal value) => Num(value, 2) + "%";
        string TextOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? Dash : value.Trim();
        // عدد داخل متنِ راست‌به‌چپ باید یک تکهٔ چپ‌به‌راست بماند، وگرنه «%» و واحد جابه‌جا می‌شوند.
        static string Chunk(string value) => $"‎{value}‎";
        string Date(DateTime value) => PdfDesignSystem.FormatPdfDate(value, isEnglish);

        static ContractJourneySummaryPdfTone MoneyTone(decimal value)
            => value < 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Neutral;

        var metrics = model.SummaryMetrics;
        var useSummaryMetrics = model.IsInitialSummaryPayload && metrics.HasValues;

        var salesCount = useSummaryMetrics ? metrics.SaleCount : model.SalesItems.Count;
        var soldQuantityMt = useSummaryMetrics ? metrics.SaleQuantityMt : model.SalesItems.Sum(s => s.QuantityMt);
        var salesTotalUsd = useSummaryMetrics ? metrics.SaleTotalUsd : model.SalesItems.Sum(s => s.AmountUsd);
        var expenseTotalUsd = useSummaryMetrics ? metrics.ExpenseTotalUsd : model.ExpenseItems.Sum(e => e.AmountUsd);
        var paymentCount = useSummaryMetrics ? metrics.PaymentCount : model.PaymentItems.Count;
        var dispatchQuantityMt = useSummaryMetrics ? metrics.DispatchQuantityMt : model.DispatchItems.Sum(d => d.LoadedQuantityMt);
        var dispatchCount = useSummaryMetrics ? metrics.DispatchCount : model.DispatchItems.Count;
        var loadingQuantityMt = useSummaryMetrics ? metrics.LoadingQuantityMt : model.LoadingItems.Sum(l => l.LoadedQuantityMt);
        var loadingCount = useSummaryMetrics ? metrics.LoadingCount : model.LoadingItems.Count;
        var loadingValueUsd = useSummaryMetrics ? metrics.LoadingValueUsd : model.LoadingItems.Sum(l => l.LoadingValueUsd ?? 0m);
        var receiptCount = useSummaryMetrics ? metrics.ReceiptCount : model.ReceiptItems.Count;
        var receiptQuantityMt = useSummaryMetrics ? metrics.ReceiptQuantityMt : model.ReceiptItems.Sum(r => r.ReceivedQuantityMt);
        var transportLegCount = useSummaryMetrics ? metrics.InventoryTransportLegCount : model.InventoryTransportLegItems.Count;
        var transportLegQuantityMt = useSummaryMetrics ? metrics.InventoryTransportQuantityMt : model.InventoryTransportLegItems.Sum(l => l.QuantityMt);
        var transportLegReceivedMt = useSummaryMetrics ? metrics.InventoryTransportReceivedMt : model.InventoryTransportLegItems.Sum(l => l.ReceivedQuantityMt);
        var transportLegShortageMt = useSummaryMetrics ? metrics.InventoryTransportShortageMt : model.InventoryTransportLegItems.Sum(l => l.ShortageQuantityMt);
        var transportLegInTransitMt = useSummaryMetrics
            ? metrics.InventoryTransportInTransitMt
            : model.InventoryTransportLegItems.Sum(TransportInTransitQuantity);
        var expenseCount = useSummaryMetrics ? metrics.ExpenseCount : model.ExpenseItems.Count;
        var lossEventCount = useSummaryMetrics ? metrics.LossCount : model.LossItems.Count;
        var totalDisplayLossMt = useSummaryMetrics
            ? metrics.LossQuantityMt
            : model.LossItems.Sum(loss => loss.DifferenceQuantityMt > 0m
                ? loss.DifferenceQuantityMt
                : Math.Max(loss.ChargeableLossMt, 0m));

        var summaryPnlExpenseTotalUsd = model.MiniPnl.TraceableExpensesUsd;
        var summaryRegisteredExpenseUsd = expenseTotalUsd;
        var summaryLoadingAndCustomsExpenseUsd = model.LoadingOperationalExpenseUsd + model.CustomsDeclarationTotalUsd;
        var summaryExpenseTotalUsd = expenseTotalUsd + summaryLoadingAndCustomsExpenseUsd;
        var summaryLossCostUsd = Math.Max(summaryPnlExpenseTotalUsd - summaryExpenseTotalUsd, 0m);
        decimal? summaryExpensePerLoadedMt = loadingQuantityMt > 0m ? summaryExpenseTotalUsd / loadingQuantityMt : null;
        decimal? salesAverageUsd = soldQuantityMt > 0m ? salesTotalUsd / soldQuantityMt : null;

        var remainingToLoadMt = Math.Max(model.ContractQuantityMt - loadingQuantityMt, 0m);
        var remainingToReceiveMt = Math.Max(loadingQuantityMt - receiptQuantityMt, 0m);
        var currentStockMt = model.Kpis.CurrentStockQuantityMt;
        var lifecycleSaleableQuantityMt = model.ContractQuantityMt - soldQuantityMt;
        var pnlOperationalMarginUsd = model.MiniPnl.GrossMarginUsd;

        var supplierPayableTotalUsd = model.IsPurchaseContract ? model.MiniPnl.TraceablePurchaseCostUsd : 0m;
        var supplierPaymentOutUsd = model.PaymentItems
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment && p.Direction == PaymentDirection.Out)
            .Sum(p => p.AmountUsd);
        var supplierReceiptInUsd = model.PaymentItems
            .Where(p => p.PaymentKind == PaymentKind.SupplierReceipt && p.Direction == PaymentDirection.In)
            .Sum(p => p.AmountUsd);
        var supplierSarrafSettledUsd = model.SarrafSettlementItems
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .Sum(s => s.SupplierReductionAmountUsd);
        var supplierPaidNetUsd = supplierPaymentOutUsd - supplierReceiptInUsd + supplierSarrafSettledUsd;
        var supplierRemainingUsd = supplierPayableTotalUsd - supplierPaidNetUsd;

        var marginPercent = salesTotalUsd > 0m
            ? Math.Round((pnlOperationalMarginUsd / salesTotalUsd) * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var returnOnInvestmentPercent = model.MiniPnl.CostOfGoodsSoldUsd > 0m
            ? Math.Round((pnlOperationalMarginUsd / model.MiniPnl.CostOfGoodsSoldUsd) * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var contractDisplayValueUsd = model.HasMixedLoadingPrices && model.LoadingsValueUsd > 0m
            ? model.LoadingsValueUsd
            : model.PricingFinalUnitPriceUsd.HasValue && model.PricingFinalUnitPriceUsd.Value > 0m
                ? model.PricingFinalUnitPriceUsd.Value * model.ContractQuantityMt
                : model.MiniPnl.TraceablePurchaseCostUsd;
        var hasContractValue = contractDisplayValueUsd > 0m;
        var contractValueText = hasContractValue ? Money(contractDisplayValueUsd) : pending;
        var contractValueUnit = hasContractValue ? UnitUsd : null;

        // وضعیت هر مرحله فقط نمایشی است و از همان اعداد بالا مشتق می‌شود.
        var loadingTone = loadingQuantityMt <= 0m
            ? ContractJourneySummaryPdfTone.Neutral
            : remainingToLoadMt > 0m ? ContractJourneySummaryPdfTone.Warning : ContractJourneySummaryPdfTone.Positive;
        var salesTone = soldQuantityMt <= 0m
            ? ContractJourneySummaryPdfTone.Neutral
            : lifecycleSaleableQuantityMt > 0m ? ContractJourneySummaryPdfTone.Warning : ContractJourneySummaryPdfTone.Positive;
        var expenseTone = summaryExpenseTotalUsd > 0m
            ? ContractJourneySummaryPdfTone.Positive
            : ContractJourneySummaryPdfTone.Neutral;
        var lossTone = totalDisplayLossMt > 0m
            ? ContractJourneySummaryPdfTone.Negative
            : ContractJourneySummaryPdfTone.Positive;
        var paymentTone = supplierRemainingUsd > 0m
            ? ContractJourneySummaryPdfTone.Negative
            : supplierPaidNetUsd > 0m || supplierPayableTotalUsd > 0m
                ? ContractJourneySummaryPdfTone.Positive
                : ContractJourneySummaryPdfTone.Neutral;
        var resultTone = soldQuantityMt <= 0m
            ? ContractJourneySummaryPdfTone.Neutral
            : pnlOperationalMarginUsd < 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive;

        string StageStatus(ContractJourneySummaryPdfTone tone) => tone switch
        {
            ContractJourneySummaryPdfTone.Positive => T("تکمیل", "Done"),
            ContractJourneySummaryPdfTone.Warning => T("در جریان", "In progress"),
            ContractJourneySummaryPdfTone.Negative => T("نیاز به توجه", "Needs attention"),
            _ => T("در انتظار", "Pending")
        };

        var partnerLabel = model.IsPurchaseContract ? T("تأمین‌کننده", "Supplier") : T("مشتری", "Customer");
        var partnerName = model.IsPurchaseContract ? model.SupplierName : model.CustomerName;
        var contractNetLabel = pnlOperationalMarginUsd < 0m
            ? T("ضرر خالص قرارداد", "Contract NET loss")
            : T("سود خالص قرارداد", "Contract NET profit");

        var stages = new List<ContractJourneySummaryPdfStage>
        {
            new(1, T("قرارداد", "Contract"), T("ثبت‌شده", "Registered"), ContractJourneySummaryPdfTone.Positive,
            [
                new(T("مقدار کل قرارداد", "Contract quantity"), Qty(model.ContractQuantityMt), unitMt),
                new(T("ارزش قرارداد", "Contract value"), contractValueText, contractValueUnit)
            ]),
            new(2, T("بارگیری و موجودی", "Loading & stock"), StageStatus(loadingTone), loadingTone,
            [
                new(T("بارگیری‌شده", "Loaded"), Qty(loadingQuantityMt), unitMt),
                new(T("موجودی فعلی", "Current stock"), Qty(currentStockMt), unitMt)
            ]),
            new(3, T("فروشات", "Sales"), StageStatus(salesTone), salesTone,
            [
                new(T("فروخته‌شده", "Sold"), Qty(soldQuantityMt), unitMt),
                new(T("قابل فروش", "Saleable"), Qty(lifecycleSaleableQuantityMt), unitMt,
                    Tone: MoneyTone(lifecycleSaleableQuantityMt))
            ]),
            new(4, T("هزینه‌ها", "Expenses"), StageStatus(expenseTone), expenseTone,
            [
                new(T("مجموع مصارف", "Total expenses"), Money(summaryExpenseTotalUsd), UnitUsd),
                new(T("اوسط مصرف بر تن", "Expense per MT"),
                    summaryExpensePerLoadedMt.HasValue ? Money(summaryExpensePerLoadedMt.Value) : Dash,
                    summaryExpensePerLoadedMt.HasValue ? $"{UnitUsd}/MT" : null)
            ]),
            new(5, T("ضایعات", "Losses"),
                totalDisplayLossMt > 0m ? T("نیاز به توجه", "Needs attention") : T("بدون ضایعه", "No loss"), lossTone,
            [
                new(T("مقدار ضایعات", "Loss quantity"), Qty(totalDisplayLossMt), unitMt,
                    Tone: totalDisplayLossMt > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Neutral),
                new(T("ارزش ضایعات", "Loss cost"), Money(summaryLossCostUsd), UnitUsd,
                    Tone: summaryLossCostUsd > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Neutral)
            ]),
            new(6, T("پرداخت‌ها", "Payments"), StageStatus(paymentTone), paymentTone,
            [
                new(T("پرداخت‌شده", "Paid"), Money(supplierPaidNetUsd), UnitUsd, Tone: MoneyTone(supplierPaidNetUsd)),
                new(T("مانده قابل پرداخت", "Remaining payable"), Money(supplierRemainingUsd), UnitUsd,
                    Tone: supplierRemainingUsd > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive)
            ]),
            new(7, T("سود و زیان", "Profit & loss"),
                soldQuantityMt <= 0m
                    ? T("در انتظار", "Pending")
                    : pnlOperationalMarginUsd < 0m ? T("زیان‌ده", "Loss") : T("سودده", "Profitable"),
                resultTone,
            [
                new(contractNetLabel, Money(pnlOperationalMarginUsd), UnitUsd, Tone: resultTone),
                new(T("حاشیه سود", "Margin"), Percent(marginPercent), null, Tone: MoneyTone(marginPercent))
            ])
        };

        var contractInfo = new List<ContractJourneySummaryPdfLine>
        {
            new(T("شماره قرارداد", "Contract no."), TextOrDash(model.ContractNumber)),
            new(T("نوع قرارداد", "Contract type"), TextOrDash(model.ContractTypeName)),
            new(T("جنس", "Product"), TextOrDash(model.ProductName)),
            new(T("تاریخ قرارداد", "Contract date"), Date(model.ContractDate)),
            new(T("دوره", "Period"),
                $"{(model.StartDate.HasValue ? Date(model.StartDate.Value) : Dash)} - {(model.EndDate.HasValue ? Date(model.EndDate.Value) : Dash)}"),
            new(T("قیمت واحد", "Unit price"), TextOrDash(model.PriceDisplay)),
            new(T("روش قیمت‌گذاری", "Pricing method"), TextOrDash(model.PricingMethodName))
        };

        var partyInfo = new List<ContractJourneySummaryPdfLine>
        {
            new(partnerLabel, TextOrDash(partnerName)),
            new(T("شرکت", "Company"), TextOrDash(model.CompanyName)),
            new(T("ارز تسویه", "Settlement currency"),
                model.RubSettlementSummary.IsRubSettlement ? "USD / RUB" : TextOrDash(model.Currency)),
            new(T("وضعیت قرارداد", "Contract status"), TextOrDash(model.StatusName)),
            new(T("وضعیت قیمت‌گذاری", "Pricing status"), TextOrDash(model.PricingStatusName))
        };

        if (!string.IsNullOrWhiteSpace(model.ParentContractNumber))
        {
            partyInfo.Add(new(T("قرارداد اصلی", "Parent contract"), model.ParentContractNumber!.Trim()));
        }

        if (model.SubContractItems.Count > 0)
        {
            partyInfo.Add(new(
                T("زیرقراردادها", "Sub-contracts"),
                model.SubContractItems.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var quantityLines = new List<ContractJourneySummaryPdfLine>
        {
            new(T("مقدار قرارداد", "Contract quantity"), Qty(model.ContractQuantityMt), unitMt),
            new(T("بارگیری‌شده", "Loaded"), Qty(loadingQuantityMt), unitMt,
                $"{loadingCount} {T("سند بارگیری", "loadings")}"),
            new(T("باقی‌مانده برای بارگیری", "Remaining to load"), Qty(remainingToLoadMt), unitMt, null,
                remainingToLoadMt > 0m ? ContractJourneySummaryPdfTone.Warning : ContractJourneySummaryPdfTone.Positive),
            new(T("رسیدشده", "Received"), Qty(receiptQuantityMt), unitMt,
                $"{receiptCount} {T("رسید", "receipts")}"),
            new(T("باقی‌مانده رسید", "Awaiting receipt"), Qty(remainingToReceiveMt), unitMt, null,
                remainingToReceiveMt > 0m ? ContractJourneySummaryPdfTone.Warning : ContractJourneySummaryPdfTone.Positive)
        };

        if (transportLegCount > 0)
        {
            quantityLines.Add(new(
                T("حمل داخلی", "Internal transport"), Qty(transportLegQuantityMt), unitMt,
                $"{transportLegCount} {T("سفر", "legs")} · {T("رسیده", "received")} {Chunk($"{Qty(transportLegReceivedMt)} MT")}"));
            quantityLines.Add(new(
                T("در مسیر", "In transit"), Qty(transportLegInTransitMt), unitMt, null,
                transportLegInTransitMt > 0m ? ContractJourneySummaryPdfTone.Warning : ContractJourneySummaryPdfTone.Neutral));
            quantityLines.Add(new(
                T("کسری حمل", "Transport shortage"), Qty(transportLegShortageMt), unitMt, null,
                transportLegShortageMt > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive));
        }

        if (dispatchCount > 0)
        {
            quantityLines.Add(new(
                T("نقل و انتقالات", "Transfers"), Qty(dispatchQuantityMt), unitMt,
                $"{dispatchCount} {T("ارسال", "dispatches")}"));
        }

        quantityLines.Add(new(T("فروخته‌شده", "Sold"), Qty(soldQuantityMt), unitMt,
            $"{salesCount} {T("فروش", "sales")}"));
        quantityLines.Add(new(T("موجودی فعلی", "Current stock"), Qty(currentStockMt), unitMt));
        quantityLines.Add(new(
            T("ضایعات", "Losses"), Qty(totalDisplayLossMt), unitMt,
            $"{lossEventCount} {T("رویداد", "events")}",
            totalDisplayLossMt > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive));

        var financeLines = new List<ContractJourneySummaryPdfLine>
        {
            new(T("ارزش قرارداد", "Contract value"), contractValueText, contractValueUnit),
            new(T("ارزش بارگیری‌ها", "Loadings value"), Money(loadingValueUsd), UnitUsd),
            new(T("مصارف ثبت‌شده", "Registered expenses"), Money(summaryRegisteredExpenseUsd), UnitUsd,
                $"{expenseCount} {T("سند", "records")}"),
            new(T("مصارف بارگیری و گمرک", "Loading & customs expenses"), Money(summaryLoadingAndCustomsExpenseUsd), UnitUsd),
            new(T("مجموع مصارف", "Total expenses"), Money(summaryExpenseTotalUsd), UnitUsd,
                summaryExpensePerLoadedMt.HasValue
                    ? $"{T("بر تن", "per MT")} {Chunk($"{Money(summaryExpensePerLoadedMt.Value)} {UnitUsd}/MT")}"
                    : null),
            new(T("ارزش ضایعات", "Loss cost"), Money(summaryLossCostUsd), UnitUsd, null,
                summaryLossCostUsd > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Neutral),
            new(T("فروش", "Sales"), Money(salesTotalUsd), UnitUsd,
                salesAverageUsd.HasValue
                    ? $"{T("اوسط قیمت", "average price")} {Chunk($"{Money(salesAverageUsd.Value)} {UnitUsd}/MT")}"
                    : null)
        };

        if (model.IsPurchaseContract)
        {
            financeLines.Add(new(
                T("قابل پرداخت به تأمین‌کننده", "Supplier payable"), Money(supplierPayableTotalUsd), UnitUsd));
            financeLines.Add(new(
                T("پرداخت‌شده", "Paid"), Money(supplierPaidNetUsd), UnitUsd,
                $"{paymentCount} {T("پرداخت", "payments")}", MoneyTone(supplierPaidNetUsd)));
            financeLines.Add(new(
                T("مانده قابل پرداخت", "Remaining payable"), Money(supplierRemainingUsd), UnitUsd, null,
                supplierRemainingUsd > 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive));
        }

        financeLines.Add(new(
            contractNetLabel,
            Money(pnlOperationalMarginUsd),
            UnitUsd,
            $"{T("حاشیه", "margin")} {Chunk(Percent(marginPercent))} · {T("بازده", "ROI")} {Chunk(Percent(returnOnInvestmentPercent))}",
            pnlOperationalMarginUsd < 0m ? ContractJourneySummaryPdfTone.Negative : ContractJourneySummaryPdfTone.Positive));

        financeLines.Add(new(
            T("سود حسابداری (محقق‌شده)", "Realised profit (accounting)"),
            Money(model.MiniPnl.Realised.GrossProfitUsd),
            UnitUsd,
            $"{T("فروش", "revenue")} {Chunk($"{Money(model.MiniPnl.Realised.RevenueUsd)} {UnitUsd}")}"
                + $" · {T("بهای تمام‌شده", "COGS")} {Chunk($"{Money(model.MiniPnl.Realised.CostOfGoodsSoldUsd)} {UnitUsd}")}"
                + (isEnglish ? string.Empty : $" · {model.MiniPnl.Realised.ConfidenceLabelFa}"),
            model.MiniPnl.Realised.GrossProfitUsd < 0m
                ? ContractJourneySummaryPdfTone.Negative
                : ContractJourneySummaryPdfTone.Positive));

        var warnings = new List<string>();
        warnings.AddRange(model.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()));
        warnings.AddRange(model.NotesForReview.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()));

        var note = string.IsNullOrWhiteSpace(model.NextRecommendedActionTitle)
            ? model.MiniPnl.Note
            : model.NextRecommendedActionTitle.Trim()
              + (string.IsNullOrWhiteSpace(model.NextRecommendedActionDescription)
                  ? string.Empty
                  : $" — {model.NextRecommendedActionDescription.Trim()}");

        var journeyName = T($"گشت قرارداد {model.ContractNumber}", $"Contract journey {model.ContractNumber}");
        var subtitleParts = new[]
        {
            model.ContractTypeName,
            model.ProductName,
            string.IsNullOrWhiteSpace(partnerName) ? null : $"{partnerLabel}: {partnerName!.Trim()}",
            model.CompanyName
        }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim());

        return new ContractJourneySummaryPdfModel
        {
            FileNameStem = $"PTG_Contract_{model.ContractNumber}_summary",
            DocumentTitle = journeyName,
            JourneyName = journeyName,
            JourneySubtitle = string.Join("  ·  ", subtitleParts),
            StatusText = TextOrDash(model.StatusName),
            StatusTone = model.StatusBadgeClass.Contains("success", StringComparison.OrdinalIgnoreCase)
                ? ContractJourneySummaryPdfTone.Positive
                : ContractJourneySummaryPdfTone.Neutral,
            CompanyName = model.CompanyName,
            GeneratedAt = generatedAt,
            HeadlineMetrics =
            [
                new(T("مقدار قرارداد", "Contract quantity"), Qty(model.ContractQuantityMt), unitMt),
                new(T("ارزش قرارداد", "Contract value"), contractValueText, contractValueUnit),
                new(T("فروخته‌شده", "Sold"), Qty(soldQuantityMt), unitMt),
                new(contractNetLabel, Money(pnlOperationalMarginUsd), UnitUsd, resultTone,
                    $"{T("حاشیه", "margin")} {Chunk(Percent(marginPercent))}")
            ],
            ContractInfo = contractInfo,
            PartyInfo = partyInfo,
            Stages = stages,
            Sections =
            [
                new(T("جریان مقدار", "Quantity flow"), quantityLines),
                new(T("خلاصه مالی", "Financial summary"), financeLines)
            ],
            Warnings = warnings,
            Note = note
        };
    }
}
