using PTG.ContractReportingRepair;
static decimal? Calculate(decimal contract = 200m, decimal loaded = 200m, decimal priced = 200m,
    decimal pending = 0m, decimal sold = 197.5m, decimal loss = 2.5m, decimal stock = 0m,
    decimal tank = 0m, decimal fx = 0m) =>
    CompletedJourneyMath.Profit(contract, loaded, priced, pending, sold, loss, stock, tank,
        296250m, 200000m, 4089m, fx);
static void Equal<T>(T expected, T actual, string test) {
    if (!Equals(expected, actual)) throw new Exception(test + ": expected " + expected + ", got " + actual);
    Console.WriteLine("PASS " + test);
}
Equal<decimal?>(92161m, Calculate(), "P002 completed profit; net freight recovery counted once");
Equal<decimal?>(null, Calculate(sold:100m), "Unsold goods do not trigger completed profit");
Equal<decimal?>(null, Calculate(loaded:199m), "Unloaded contract quantity prevents completion");
Equal<decimal?>(null, Calculate(priced:199m), "Unpriced goods prevent completion");
Equal<decimal?>(null, Calculate(pending:1m), "Pending purchase prevents completion");
Equal<decimal?>(null, Calculate(stock:1m), "Remaining inventory prevents completion");
Equal<decimal?>(null, Calculate(tank:1m), "Pending tank settlement prevents completion");
Equal<decimal?>(92261m, Calculate(fx:100m), "Realised FX included once");
Equal(400m, CompletedJourneyMath.ActualFreightRecovery(990m,590m,400m), "First driver recovery");
Equal(300m, CompletedJourneyMath.ActualFreightRecovery(1000m,700m,300m), "Second driver recovery");
Equal(100m, CompletedJourneyMath.ActualFreightRecovery(100m,0m,400m), "Recovery capped at actual freight reduction");
Equal(0m, CompletedJourneyMath.ActualFreightRecovery(100m,100m,400m), "Claim without deduction is not recovery");
Equal(0m, CompletedJourneyMath.ActualFreightRecovery(100m,null,400m), "Unsettled deduction is not recovery");
Console.WriteLine("All 13 reporting regression checks passed.");
