using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record SalesAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface ISalesAccountingAdapter
{
    /// <summary>
    /// Posts the revenue a sale earned and, separately, what the goods it moved cost.
    /// </summary>
    Task<SalesAccountingResult> TryPostSaleAsync(
        SalesTransaction sale,
        CancellationToken cancellationToken = default);

    Task<SalesAccountingResult> TryPostCogsAsync(
        SalesTransaction sale,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses the revenue journal of a cancelled sale with an official, balanced counter-journal.
    /// Nothing is deleted and a second call is a no-op.
    /// </summary>
    Task<SalesAccountingResult> TryReverseSaleAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses the cost journal of a cancelled sale and gives the value back to the valuation
    /// pool, matching the compensating inbound movement the legacy cancel writes.
    /// </summary>
    Task<SalesAccountingResult> TryReverseCogsAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a customer payment allocation to deliveries that already posted with an open
    /// receivable (allocation booked after delivery). Each delivery gets a traceable application
    /// and its own balanced transfer journal (customer advance Dr, accounts receivable Cr).
    /// Anything left over after the oldest open deliveries are settled stays free for future ones.
    /// </summary>
    Task<int> TrySettleDeliveredReceivableAsync(
        int customerPaymentAllocationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases every active advance application of a cancelled delivery: reverses the independent
    /// transfer journal of after-delivery applications and marks all of them reversed, so the
    /// consumed advance becomes free again. Idempotent — a second call finds nothing active.
    /// </summary>
    Task<int> TryReleaseAdvanceApplicationsAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 7 dual-write pilot for sales and cost of goods sold.
///
///   Sale  Dr Accounts Receivable  Cr Sales Revenue        (party = customer)
///   Cogs  Dr Cost of Goods Sold   Cr Inventory
///
/// The two are separate journals behind separate flags on purpose. Revenue is known the moment
/// the sale is written; cost is only known once the goods that left have been valued, and that
/// depends on the purchase side having posted first. Splitting them means a sale is never held
/// hostage to its cost — and the log shows exactly which sales are carrying revenue without
/// cost yet.
///
/// Cost comes from <see cref="IInventoryValuationService"/>, the single valuation authority,
/// which holds a moving weighted average per (company, product, terminal). The quantity and
/// terminal come from the legacy outbound InventoryMovement rows the sale already wrote, so the
/// journal values exactly what the operational system says left the tank — never a re-derived
/// quantity. A sale spanning several terminals consumes from each pool in turn.
///
/// When a pool cannot cover what left it, COGS is skipped with INVENTORY_NOT_VALUED and the
/// revenue still posts. Profit reads high until the matching purchase is posted, which is
/// visible and recoverable; guessing a cost would not be.
/// </summary>
public sealed class SalesAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IInventoryValuationService valuation,
    IOptions<AccountingOptions> options,
    ILogger<SalesAccountingAdapter> logger)
    : ISalesAccountingAdapter
{
    public const string SourceModule = "Sale";
    public const string SourceEntityType = nameof(SalesTransaction);

    private readonly AccountingOptions _options = options.Value;

    public async Task<SalesAccountingResult> TryPostSaleAsync(
        SalesTransaction sale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!_options.Enabled)
            return Skipped(sale, "Sale", 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Sale)
            return Skipped(sale, "Sale", 0, "PILOT_DISABLED");

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(sale, cancellationToken);
        if (skipReason is not null)
            return Skipped(sale, "Sale", companyId, skipReason);

        var sourceEventId = BuildCreatedSourceEventId(sale.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(sale, "Sale", companyId, sale.TotalUsd,
                existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SalesAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);
        var rate = sale.AppliedFxRateToUsd!.Value;

        // یک تحویل پیش‌فروش می‌تواند پیش‌دریافتِ تخصیص‌یافته داشته باشد؛ آن بخش به جای مطالبات،
        // بدهیِ پیش‌دریافت مشتری را مصرف می‌کند. مصرف واقعی است و ردیابی می‌شود: هر بند از یک
        // CustomerPaymentAllocation فعال گرفته می‌شود و بعد از ثبتِ ژورنال به Application تبدیل
        // می‌شود. فروش عادی (بدون PreSaleOrderId یا بدون تخصیص) دست‌نخورده می‌ماند.
        var plan = await BuildAdvancePlanAsync(sale, settings, companyId, cancellationToken);
        var advanceUsd = plan.Sum(x => x.AppliedAmountUsd);
        var receivableUsd = decimal.Round(sale.TotalUsd - advanceUsd, 4, MidpointRounding.AwayFromZero);

        var lines = new List<AccountingPostLine>();

        // پیش‌دریافتِ مصرف‌شده به ارزشِ تاریخیِ USD (نرخ روزِ دریافت). چون در ارزِ پایه ثبت می‌شود،
        // خطِ آن همیشه دقیقاً تطبیق می‌یابد و هیچ fallbackِ خاموشی لازم نیست.
        if (advanceUsd > 0m)
        {
            lines.Add(new AccountingPostLine(
                settings.CustomerAdvanceAccountId,
                Debit: advanceUsd,
                Credit: 0m,
                SystemCurrency.BaseCurrencyCode,
                advanceUsd,
                1m,
                AccountingPartyType.Customer,
                sale.CustomerId,
                ContractId: sale.ContractId,
                ShipmentId: sale.ShipmentId,
                ProductId: sale.ProductId,
                Description: $"Customer advance applied to invoice {sale.InvoiceNumber}"));
        }

        if (receivableUsd > 0m)
        {
            // بدونِ پیش‌دریافت، مطالبات همان ارزِ فروش را نگه می‌دارد (رفتار فروشِ عادی، بدون تغییر).
            // با پیش‌دریافت، باقیماندهٔ مطالبات هم در ارزِ پایه ثبت می‌شود تا با بخشِ USDِ پیش‌دریافت
            // هم‌واحد و همیشه متوازن باشد؛ درآمد همچنان ارز و ارزشِ فروش را نشان می‌دهد.
            lines.Add(advanceUsd > 0m
                ? new AccountingPostLine(
                    settings.AccountsReceivableAccountId,
                    Debit: receivableUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    receivableUsd,
                    1m,
                    AccountingPartyType.Customer,
                    sale.CustomerId,
                    ContractId: sale.ContractId,
                    ShipmentId: sale.ShipmentId,
                    ProductId: sale.ProductId,
                    Description: $"Sale invoice {sale.InvoiceNumber}")
                : new AccountingPostLine(
                    settings.AccountsReceivableAccountId,
                    Debit: receivableUsd,
                    Credit: 0m,
                    sale.Currency,
                    sale.TotalInCurrency,
                    rate,
                    AccountingPartyType.Customer,
                    sale.CustomerId,
                    ContractId: sale.ContractId,
                    ShipmentId: sale.ShipmentId,
                    ProductId: sale.ProductId,
                    Description: $"Sale invoice {sale.InvoiceNumber}"));
        }

        lines.Add(new AccountingPostLine(
            settings.SalesRevenueAccountId,
            Debit: 0m,
            Credit: sale.TotalUsd,
            sale.Currency,
            sale.TotalInCurrency,
            rate,
            ContractId: sale.ContractId,
            ShipmentId: sale.ShipmentId,
            ProductId: sale.ProductId,
            Description: "Sales revenue"));

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForSale(companyId, sale.Id),
            sale.SaleDate.Date,
            sale.SaleDate.Date,
            sale.SaleDate.Date,
            SourceModule,
            lines,
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: sale.Id,
            Description: $"Sale #{sale.Id} invoice {sale.InvoiceNumber} on {sale.SaleDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);

            // پس از ثبتِ ژورنال، هر بندِ مصرف به یک Applicationِ ردیابی‌پذیر تبدیل می‌شود. اثر مالیِ
            // این مصرف داخلِ همین ژورنالِ تحویل است، پس JournalEntryId خالی می‌ماند.
            if (plan.Count > 0)
            {
                foreach (var item in plan)
                {
                    db.CustomerPaymentAllocationApplications.Add(new CustomerPaymentAllocationApplication
                    {
                        CustomerPaymentAllocationId = item.AllocationId,
                        SalesTransactionId = sale.Id,
                        AppliedAt = sale.SaleDate.Date,
                        AppliedPaymentAmount = item.AppliedPaymentAmount,
                        PaymentCurrencyCode = item.PaymentCurrencyCode,
                        AppliedAmountUsd = item.AppliedAmountUsd,
                        Status = CustomerPaymentAllocationApplicationStatus.Active,
                        CompanyId = companyId,
                        JournalEntryId = null
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            LogOutcome(sale, "Sale", companyId, sale.TotalUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new SalesAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(sale, "Sale", exception);
            throw;
        }
    }

    private sealed record AdvancePlanItem(
        int AllocationId,
        decimal AppliedAmountUsd,
        decimal AppliedPaymentAmount,
        string PaymentCurrencyCode);

    /// <summary>
    /// The traceable plan for how much of each active allocation this delivery consumes. Money
    /// allocated to the pre-sale sits on the customer advance account as a historical-USD
    /// liability; the delivery consumes the still-free part of each allocation, oldest first, up
    /// to its own value, and only the rest becomes a receivable.
    ///
    /// The unconsumed balance of an allocation is real data — its USD value minus the sum of its
    /// active applications — not a guess from delivery order, so a second delivery can never spend
    /// what an application already recorded as spent.
    /// </summary>
    private async Task<List<AdvancePlanItem>> BuildAdvancePlanAsync(
        SalesTransaction sale,
        AccountingSettings settings,
        int companyId,
        CancellationToken cancellationToken)
    {
        var plan = new List<AdvancePlanItem>();
        if (!sale.PreSaleOrderId.HasValue)
            return plan;

        var advanceAccountUsable = await db.Accounts
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == settings.CustomerAdvanceAccountId
                    && x.CompanyId == companyId
                    && x.IsActive,
                cancellationToken);
        if (!advanceAccountUsable)
            return plan;

        await LockAllocationsAsync(sale.PreSaleOrderId.Value, cancellationToken);

        var allocations = await db.CustomerPaymentAllocations
            .Where(x => x.PreSaleOrderId == sale.PreSaleOrderId.Value
                && x.Status == CustomerPaymentAllocationStatus.Active)
            .OrderBy(x => x.AllocationDate).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.AllocatedAmountUsd,
                x.PaymentCurrencyCode,
                x.PaymentFxRateToUsd,
                Consumed = db.CustomerPaymentAllocationApplications
                    .Where(a => a.CustomerPaymentAllocationId == x.Id
                        && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
                    .Sum(a => (decimal?)a.AppliedAmountUsd) ?? 0m
            })
            .ToListAsync(cancellationToken);

        var remaining = sale.TotalUsd;
        foreach (var allocation in allocations)
        {
            if (remaining <= 0m)
                break;

            var free = decimal.Round(allocation.AllocatedAmountUsd - allocation.Consumed, 4, MidpointRounding.AwayFromZero);
            if (free <= 0m)
                continue;

            var take = Math.Min(free, remaining);
            if (take <= 0m)
                continue;

            var paymentAmount = allocation.PaymentFxRateToUsd > 0m
                ? decimal.Round(take / allocation.PaymentFxRateToUsd, 4, MidpointRounding.AwayFromZero)
                : take;

            plan.Add(new AdvancePlanItem(allocation.Id, take, paymentAmount, allocation.PaymentCurrencyCode));
            remaining -= take;
        }

        return plan;
    }

    private async Task LockAllocationsAsync(int preSaleOrderId, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational()
            && string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT 1 FROM ""CustomerPaymentAllocations"" WHERE ""PreSaleOrderId"" = {preSaleOrderId} AND ""Status"" = 1 FOR UPDATE",
                cancellationToken);
        }
    }

    public async Task<SalesAccountingResult> TryPostCogsAsync(
        SalesTransaction sale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!_options.Enabled)
            return Skipped(sale, "Cogs", 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Cogs)
            return Skipped(sale, "Cogs", 0, "PILOT_DISABLED");

        var (companyId, skipReason) = await ResolveCompanyAndSkipReasonAsync(sale, cancellationToken);
        if (skipReason is not null)
            return Skipped(sale, "Cogs", companyId, skipReason);

        var sourceEventId = BuildCogsSourceEventId(sale.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(sale, "Cogs", companyId, 0m,
                existing.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SalesAccountingResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        // What actually left the tanks, as the operational system recorded it.
        var outMovements = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.SalesTransactionId == sale.Id && x.Direction == MovementDirection.Out)
            .Select(x => new { x.TerminalId, x.ProductId, x.QuantityMt })
            .ToListAsync(cancellationToken);
        if (outMovements.Count == 0)
            return Skipped(sale, "Cogs", companyId, "NO_OUTBOUND_MOVEMENT");
        if (outMovements.Any(x => x.QuantityMt <= 0m))
            return Skipped(sale, "Cogs", companyId, "INVALID_MOVEMENT_QUANTITY");

        // Value every pool first. If any of them cannot cover its share, nothing is consumed:
        // a half-valued sale would be worse than an unvalued one.
        var pools = outMovements
            .GroupBy(x => new { x.TerminalId, x.ProductId })
            .Select(g => new { g.Key.TerminalId, g.Key.ProductId, QuantityMt = g.Sum(x => x.QuantityMt) })
            .ToList();

        foreach (var pool in pools)
        {
            var available = await db.InventoryAverageCosts
                .AsNoTracking()
                .Where(x => x.CompanyId == companyId
                    && x.ProductId == pool.ProductId
                    && x.TerminalId == pool.TerminalId)
                .Select(x => (decimal?)x.QuantityMt)
                .SingleOrDefaultAsync(cancellationToken);
            if (available is null || available.Value < pool.QuantityMt)
                return Skipped(sale, "Cogs", companyId, "INVENTORY_NOT_VALUED");
        }

        var consumed = new List<(int TerminalId, int ProductId, decimal CostUsd)>();
        foreach (var pool in pools)
        {
            var consumption = await valuation.TryConsumeAsync(
                companyId,
                pool.ProductId,
                pool.TerminalId,
                pool.QuantityMt,
                cancellationToken);
            if (!consumption.Succeeded)
            {
                // Put back whatever the earlier pools already gave up, so a failure here leaves
                // the valuation exactly as it was.
                foreach (var done in consumed)
                {
                    var quantity = pools.Single(p =>
                        p.TerminalId == done.TerminalId && p.ProductId == done.ProductId).QuantityMt;
                    await valuation.ReturnAsync(
                        companyId, done.ProductId, done.TerminalId, quantity, done.CostUsd, cancellationToken);
                }

                return Skipped(sale, "Cogs", companyId, consumption.Reason ?? "INVENTORY_NOT_VALUED");
            }

            consumed.Add((pool.TerminalId, pool.ProductId, consumption.CostUsd));
        }

        var totalCostUsd = consumed.Sum(x => x.CostUsd);
        if (totalCostUsd <= 0m)
            return Skipped(sale, "Cogs", companyId, "INVENTORY_NOT_VALUED");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForCogs(companyId, sale.Id),
            sale.SaleDate.Date,
            sale.SaleDate.Date,
            sale.SaleDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    settings.CostOfGoodsSoldAccountId,
                    Debit: totalCostUsd,
                    Credit: 0m,
                    SystemCurrency.BaseCurrencyCode,
                    totalCostUsd,
                    1m,
                    ContractId: sale.ContractId,
                    ShipmentId: sale.ShipmentId,
                    ProductId: sale.ProductId,
                    Description: $"Cost of goods sold for invoice {sale.InvoiceNumber}"),
                new AccountingPostLine(
                    settings.InventoryAccountId,
                    Debit: 0m,
                    Credit: totalCostUsd,
                    SystemCurrency.BaseCurrencyCode,
                    totalCostUsd,
                    1m,
                    ContractId: sale.ContractId,
                    ShipmentId: sale.ShipmentId,
                    ProductId: sale.ProductId,
                    Description: "Goods left inventory")
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: sale.Id,
            Description: $"COGS for sale #{sale.Id} invoice {sale.InvoiceNumber}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);

            // بهای واقعیِ هر pool قفل می‌شود تا برگشتِ COGS دقیقاً همان مقدار و همان ارزش را به همان
            // pool برگرداند — بدون تقسیمِ تقریبیِ بهای کل بر اساس نسبتِ مقدار.
            foreach (var item in consumed)
            {
                var quantity = pools.Single(p =>
                    p.TerminalId == item.TerminalId && p.ProductId == item.ProductId).QuantityMt;
                db.SalesCostConsumptions.Add(new SalesCostConsumption
                {
                    SalesTransactionId = sale.Id,
                    CompanyId = companyId,
                    ProductId = item.ProductId,
                    TerminalId = item.TerminalId,
                    QuantityMt = quantity,
                    CostUsd = item.CostUsd,
                    Status = SalesCostConsumptionStatus.Active
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            LogOutcome(sale, "Cogs", companyId, totalCostUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new SalesAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(sale, "Cogs", exception);
            throw;
        }
    }

    /// <summary>
    /// Cancelling a sale used to leave the new ledger untouched: the legacy books got a
    /// compensating row while the journal kept revenue, receivable and cost on the accounts
    /// forever. The reversal below closes that gap through the central posting service, so the
    /// counter-journal is balanced, linked to the original and impossible to post twice.
    /// </summary>
    public async Task<SalesAccountingResult> TryReverseSaleAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!_options.Enabled)
            return Skipped(sale, "SaleReversal", 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Sale)
            return Skipped(sale, "SaleReversal", 0, "PILOT_DISABLED");

        // The company check deliberately skips the amount/FX validation of the posting path:
        // the sale is cancelled by now, and what must be undone is the journal that already exists.
        var companyId = await ResolveCompanyAsync(sale, cancellationToken);
        if (companyId is null)
            return Skipped(sale, "SaleReversal", 0, "SALE_COMPANY_UNKNOWN");

        var reversedEventId = BuildReversedSourceEventId(sale.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(sale, "SaleReversal", companyId.Value, 0m,
                existingReversal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SalesAccountingResult(
                PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildCreatedSourceEventId(sale.Id), cancellationToken);
        if (original is null)
        {
            // The sale was legacy-only (pilot off or skipped); its cancellation stays legacy-only.
            return Skipped(sale, "SaleReversal", companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");
        }

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForSaleReversal(companyId.Value, sale.Id),
            reversalDate.Date,
            SourceModule,
            reversedEventId,
            Description: $"Reversal of sale #{sale.Id} invoice {sale.InvoiceNumber}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            LogOutcome(sale, "SaleReversal", companyId.Value, sale.TotalUsd,
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new SalesAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(sale, "SaleReversal", exception);
            throw;
        }
    }

    public async Task<SalesAccountingResult> TryReverseCogsAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!_options.Enabled)
            return Skipped(sale, "CogsReversal", 0, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.Cogs)
            return Skipped(sale, "CogsReversal", 0, "PILOT_DISABLED");

        var companyId = await ResolveCompanyAsync(sale, cancellationToken);
        if (companyId is null)
            return Skipped(sale, "CogsReversal", 0, "SALE_COMPANY_UNKNOWN");

        var reversedEventId = BuildCogsReversedSourceEventId(sale.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(sale, "CogsReversal", companyId.Value, 0m,
                existingReversal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new SalesAccountingResult(
                PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(
            companyId.Value, BuildCogsSourceEventId(sale.Id), cancellationToken);
        if (original is null)
            return Skipped(sale, "CogsReversal", companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        // بهای واقعیِ هر pool که هنگام فروش قفل شد، مبنای دقیقِ برگشت است.
        var consumptions = await db.SalesCostConsumptions
            .Where(x => x.SalesTransactionId == sale.Id
                && x.Status == SalesCostConsumptionStatus.Active)
            .ToListAsync(cancellationToken);

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForCogsReversal(companyId.Value, sale.Id),
            reversalDate.Date,
            SourceModule,
            reversedEventId,
            Description: $"Reversal of COGS for sale #{sale.Id} invoice {sale.InvoiceNumber}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);

            if (consumptions.Count > 0)
            {
                // مسیرِ دقیق: هر pool دقیقاً همان مقدار و همان ارزشی را که مصرف کرد پس می‌گیرد.
                foreach (var consumption in consumptions)
                {
                    await valuation.ReturnAsync(
                        companyId.Value,
                        consumption.ProductId,
                        consumption.TerminalId,
                        consumption.QuantityMt,
                        consumption.CostUsd,
                        cancellationToken);

                    consumption.Status = SalesCostConsumptionStatus.Reversed;
                    consumption.ReversedAtUtc = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // مسیرِ سازگاریِ عقب‌رو: فروش‌هایی که COGSشان پیش از افزودنِ SalesCostConsumption ثبت
                // شده جزئیاتِ per-pool ندارند؛ برای این‌ها بهای کل بر اساس نسبتِ مقدارِ حرکاتِ خروجی
                // تقسیم می‌شود تا همچنان قابلِ برگشت بمانند.
                await ReturnByMovementSplitAsync(sale, companyId.Value, original.Lines.Sum(x => x.Debit), cancellationToken);
            }

            LogOutcome(sale, "CogsReversal", companyId.Value, original.Lines.Sum(x => x.Debit),
                journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new SalesAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(sale, "CogsReversal", exception);
            throw;
        }
    }

    private async Task ReturnByMovementSplitAsync(
        SalesTransaction sale,
        int companyId,
        decimal totalCostUsd,
        CancellationToken cancellationToken)
    {
        var pools = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.SalesTransactionId == sale.Id && x.Direction == MovementDirection.Out)
            .GroupBy(x => new { x.TerminalId, x.ProductId })
            .Select(g => new { g.Key.TerminalId, g.Key.ProductId, QuantityMt = g.Sum(x => x.QuantityMt) })
            .ToListAsync(cancellationToken);

        var totalQuantityMt = pools.Sum(x => x.QuantityMt);
        if (pools.Count == 0 || totalQuantityMt <= 0m)
            return;

        var returned = 0m;
        for (var index = 0; index < pools.Count; index++)
        {
            var pool = pools[index];
            var costShare = index == pools.Count - 1
                ? totalCostUsd - returned
                : decimal.Round(totalCostUsd * (pool.QuantityMt / totalQuantityMt), 4, MidpointRounding.AwayFromZero);
            returned += costShare;

            await valuation.ReturnAsync(
                companyId, pool.ProductId, pool.TerminalId, pool.QuantityMt, costShare, cancellationToken);
        }
    }

    public static string BuildCreatedSourceEventId(int salesTransactionId)
        => $"Sale:{salesTransactionId}:Created";

    public static string BuildReversedSourceEventId(int salesTransactionId)
        => $"Sale:{salesTransactionId}:Reversed";

    public static string BuildCogsSourceEventId(int salesTransactionId)
        => $"Sale:{salesTransactionId}:Cogs";

    public static string BuildCogsReversedSourceEventId(int salesTransactionId)
        => $"Sale:{salesTransactionId}:CogsReversed";

    public static string BuildAdvanceApplicationSourceEventId(int applicationId)
        => $"CustomerAdvanceApplication:{applicationId}:Created";

    public static string BuildAdvanceApplicationReversedSourceEventId(int applicationId)
        => $"CustomerAdvanceApplication:{applicationId}:Reversed";

    public async Task<int> TrySettleDeliveredReceivableAsync(
        int customerPaymentAllocationId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.Pilots.Sale)
            return 0;

        var allocation = await db.CustomerPaymentAllocations
            .FirstOrDefaultAsync(x => x.Id == customerPaymentAllocationId, cancellationToken);
        if (allocation is null || allocation.Status != CustomerPaymentAllocationStatus.Active)
            return 0;

        var consumed = await db.CustomerPaymentAllocationApplications
            .Where(a => a.CustomerPaymentAllocationId == allocation.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .SumAsync(a => (decimal?)a.AppliedAmountUsd, cancellationToken) ?? 0m;
        var free = decimal.Round(allocation.AllocatedAmountUsd - consumed, 4, MidpointRounding.AwayFromZero);
        if (free <= 0m)
            return 0;

        // تحویل‌های همان پیش‌فروش که لغو نشده‌اند و طلبِ باز دارند، از قدیمی‌ترین تحویل.
        var deliveries = await db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.PreSaleOrderId == allocation.PreSaleOrderId && !s.IsCancelled)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.TotalUsd, s.CompanyId, s.SourcePurchaseContractId, s.ContractId, s.CustomerId, s.InvoiceNumber })
            .ToListAsync(cancellationToken);
        if (deliveries.Count == 0)
            return 0;

        var applied = 0;
        foreach (var delivery in deliveries)
        {
            if (free <= 0m)
                break;

            // فقط تحویلی که ژورنالِ فروشش ثبت شده طلبِ واقعی روی حساب دارد.
            var saleJournal = await FindJournalAsync(
                await ResolveDeliveryCompanyAsync(delivery.CompanyId, delivery.SourcePurchaseContractId, delivery.ContractId, cancellationToken) ?? 0,
                BuildCreatedSourceEventId(delivery.Id),
                cancellationToken);
            if (saleJournal is null)
                continue;

            var companyId = saleJournal.CompanyId;

            var advanceAccountUsable = await db.AccountingSettings
                .AsNoTracking()
                .Where(x => x.CompanyId == companyId)
                .Select(x => x.CustomerAdvanceAccountId)
                .SingleOrDefaultAsync(cancellationToken);
            if (advanceAccountUsable == 0)
                continue;

            var settings = await db.AccountingSettings.AsNoTracking()
                .SingleAsync(x => x.CompanyId == companyId, cancellationToken);

            var alreadyApplied = await db.CustomerPaymentAllocationApplications
                .Where(a => a.SalesTransactionId == delivery.Id
                    && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
                .SumAsync(a => (decimal?)a.AppliedAmountUsd, cancellationToken) ?? 0m;
            var openReceivable = decimal.Round(delivery.TotalUsd - alreadyApplied, 4, MidpointRounding.AwayFromZero);
            if (openReceivable <= 0m)
                continue;

            var take = Math.Min(free, openReceivable);
            if (take <= 0m)
                continue;

            var paymentAmount = allocation.PaymentFxRateToUsd > 0m
                ? decimal.Round(take / allocation.PaymentFxRateToUsd, 4, MidpointRounding.AwayFromZero)
                : take;

            var application = new CustomerPaymentAllocationApplication
            {
                CustomerPaymentAllocationId = allocation.Id,
                SalesTransactionId = delivery.Id,
                AppliedAt = AfghanistanBusinessClock.SystemToday,
                AppliedPaymentAmount = paymentAmount,
                PaymentCurrencyCode = allocation.PaymentCurrencyCode,
                AppliedAmountUsd = take,
                Status = CustomerPaymentAllocationApplicationStatus.Active,
                CompanyId = companyId
            };
            db.CustomerPaymentAllocationApplications.Add(application);
            await db.SaveChangesAsync(cancellationToken);

            // ژورنالِ انتقالِ مستقلِ متوازن: پیش‌دریافت مشتری بدهکار، مطالبات مشتری بستانکار (هر دو USD).
            var request = new AccountingPostRequest(
                companyId,
                journalNumberGenerator.ForCustomerAdvanceApplication(companyId, application.Id),
                application.AppliedAt,
                application.AppliedAt,
                application.AppliedAt,
                SourceModule,
                new[]
                {
                    new AccountingPostLine(
                        settings.CustomerAdvanceAccountId,
                        Debit: take,
                        Credit: 0m,
                        SystemCurrency.BaseCurrencyCode,
                        take,
                        1m,
                        AccountingPartyType.Customer,
                        delivery.CustomerId,
                        Description: $"Advance applied to invoice {delivery.InvoiceNumber} after delivery"),
                    new AccountingPostLine(
                        settings.AccountsReceivableAccountId,
                        Debit: 0m,
                        Credit: take,
                        SystemCurrency.BaseCurrencyCode,
                        take,
                        1m,
                        AccountingPartyType.Customer,
                        delivery.CustomerId,
                        Description: $"Receivable settled by advance for invoice {delivery.InvoiceNumber}")
                },
                SourceEventId: BuildAdvanceApplicationSourceEventId(application.Id),
                SourceEntityType: nameof(CustomerPaymentAllocationApplication),
                SourceEntityId: application.Id,
                Description: $"Customer advance applied to delivery #{delivery.Id} after posting");

            var journal = await postingService.PostAsync(request, cancellationToken);
            application.JournalEntryId = journal.Id;
            await db.SaveChangesAsync(cancellationToken);

            free = decimal.Round(free - take, 4, MidpointRounding.AwayFromZero);
            applied++;
        }

        return applied;
    }

    public async Task<int> TryReleaseAdvanceApplicationsAsync(
        SalesTransaction sale,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!_options.Enabled || !_options.Pilots.Sale)
            return 0;

        var applications = await db.CustomerPaymentAllocationApplications
            .Where(a => a.SalesTransactionId == sale.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .ToListAsync(cancellationToken);
        if (applications.Count == 0)
            return 0;

        foreach (var application in applications)
        {
            // مصرفِ «بعد از تحویل» ژورنالِ مستقل دارد و باید رسماً معکوس شود؛ مصرفِ «هنگام تحویل»
            // اثرش داخلِ ژورنالِ فروش بود که همین‌جا در لغو معکوس می‌شود، پس ژورنالِ جدا ندارد.
            if (application.JournalEntryId.HasValue)
            {
                var companyId = application.CompanyId
                    ?? await ResolveCompanyAsync(sale, cancellationToken)
                    ?? 0;
                if (companyId > 0)
                {
                    var reversedEvent = BuildAdvanceApplicationReversedSourceEventId(application.Id);
                    var existingReversal = await FindJournalAsync(companyId, reversedEvent, cancellationToken);
                    if (existingReversal is null)
                    {
                        var reversalRequest = new AccountingReversalRequest(
                            application.JournalEntryId.Value,
                            journalNumberGenerator.ForCustomerAdvanceApplicationReversal(companyId, application.Id),
                            reversalDate.Date,
                            SourceModule,
                            reversedEvent,
                            Description: $"Reversal of advance application #{application.Id} (delivery cancelled)");
                        await postingService.ReverseAsync(reversalRequest, cancellationToken);
                    }
                }
            }

            application.Status = CustomerPaymentAllocationApplicationStatus.Reversed;
            application.ReversedAtUtc = DateTime.UtcNow;
            application.ReversalReason = "Delivery cancelled";
        }

        await db.SaveChangesAsync(cancellationToken);
        return applications.Count;
    }

    private async Task<int?> ResolveDeliveryCompanyAsync(
        int? companyId,
        int? sourcePurchaseContractId,
        int? contractId,
        CancellationToken cancellationToken)
    {
        if (companyId.HasValue)
            return companyId;

        foreach (var id in new[] { sourcePurchaseContractId, contractId })
        {
            if (!id.HasValue)
                continue;
            var resolved = await db.Contracts.AsNoTracking()
                .Where(x => x.Id == id.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (resolved.HasValue)
                return resolved;
        }

        return null;
    }

    private async Task<(int CompanyId, string? SkipReason)> ResolveCompanyAndSkipReasonAsync(
        SalesTransaction sale,
        CancellationToken cancellationToken)
    {
        if (sale.IsCancelled)
            return (0, "SALE_CANCELLED");
        if (sale.QuantityMt <= 0m)
            return (0, "INVALID_SALE_QUANTITY");
        if (sale.TotalUsd <= 0m || sale.TotalInCurrency <= 0m)
            return (0, "INVALID_SALE_AMOUNT");

        var rate = sale.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
            return (0, "INVALID_SALE_FX");
        if (SystemCurrency.IsBaseCurrency(sale.Currency) && rate.Value != 1m)
            return (0, "INVALID_SALE_FX");

        var expectedUsd = decimal.Round(sale.TotalInCurrency * rate.Value, 4, MidpointRounding.AwayFromZero);
        if (sale.TotalUsd != expectedUsd)
            return (0, "INVALID_SALE_CONVERSION");

        var companyId = await ResolveCompanyAsync(sale, cancellationToken);
        if (companyId is null)
            return (0, "SALE_COMPANY_UNKNOWN");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var accountIds = new[]
        {
            settings.AccountsReceivableAccountId,
            settings.SalesRevenueAccountId,
            settings.CostOfGoodsSoldAccountId,
            settings.InventoryAccountId
        };
        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Distinct().Count())
            return (companyId.Value, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, null);
    }

    /// <summary>
    /// A sale's company, when provable. SalesTransaction.CompanyId is nullable by design — a
    /// bulk sale of a shipment whose contracts belong to different companies has no single owner
    /// — so the source purchase contract and then the sale's own contract are the fallbacks.
    /// Anything else stays unresolved rather than guessed.
    /// </summary>
    private async Task<int?> ResolveCompanyAsync(SalesTransaction sale, CancellationToken cancellationToken)
    {
        if (sale.CompanyId.HasValue)
            return sale.CompanyId;

        foreach (var contractId in new[] { sale.SourcePurchaseContractId, sale.ContractId })
        {
            if (!contractId.HasValue)
                continue;

            var companyId = await db.Contracts
                .AsNoTracking()
                .Where(x => x.Id == contractId.Value)
                .Select(x => (int?)x.CompanyId)
                .SingleOrDefaultAsync(cancellationToken);
            if (companyId.HasValue)
                return companyId;
        }

        return null;
    }

    private async Task<JournalEntry?> FindJournalAsync(
        int companyId,
        string sourceEventId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);

    private SalesAccountingResult Skipped(
        SalesTransaction sale,
        string eventKind,
        int companyId,
        string reason)
    {
        LogOutcome(sale, eventKind, companyId, 0m, 0m, PaymentPostingStatus.Skipped, reason);
        return new SalesAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        SalesTransaction sale,
        string eventKind,
        int companyId,
        decimal expectedAmountUsd,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
    {
        // Legacy writes one sale ledger row of TotalUsd and nothing at all for cost, so revenue
        // reconciles against it and COGS has no legacy counterpart to compare with.
        logger.LogInformation(
            "Sales accounting pilot comparison: SaleId {SaleId}, EventKind {EventKind}, CompanyId {CompanyId}, CustomerId {CustomerId}, LegacyTotalUsd {LegacyTotalUsd}, ExpectedAmountUsd {ExpectedAmountUsd}, JournalDebitTotal {JournalDebitTotal}, Difference {Difference}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            sale.Id,
            eventKind,
            companyId,
            sale.CustomerId,
            sale.TotalUsd,
            expectedAmountUsd,
            journalDebitTotal,
            journalDebitTotal - expectedAmountUsd,
            status,
            reason);
    }

    private void LogFailure(SalesTransaction sale, string eventKind, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Sales accounting pilot posting failed for SaleId {SaleId} ({EventKind}) with FailureReason {FailureReason}",
            sale.Id,
            eventKind,
            failureReason);
    }
}
