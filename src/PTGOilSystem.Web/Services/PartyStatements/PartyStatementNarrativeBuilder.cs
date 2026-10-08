using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// شرح تجارتیِ سطرهای صورت‌حساب را از سند اصلی می‌سازد: «فروش دیزل / موتر ۱۲۳۴۵ / فاکتور INV001»
/// با مقدار و نرخ فی واحد در ستون‌های جدا. فقط فیلدهای نمایشی پر می‌شود؛ هیچ مبلغ، جهت،
/// ترتیب یا بیلانسی تغییر نمی‌کند. هر نوع سند با یک کوئری projection خوانده می‌شود (بدون N+1).
///
/// قاعدهٔ «حدس ممنوع»: نرخ فقط وقتی نشان داده می‌شود که مقدار × نرخ با مبلغ همان سطر
/// برابر باشد؛ موتر فقط از پیوند مستقیم سند؛ اگر سند اصلی پیدا نشود Title خالی می‌ماند
/// و همان شرح دفتر نمایش داده می‌شود.
/// </summary>
internal sealed class PartyStatementNarrativeBuilder(ApplicationDbContext db)
{
    private const string Separator = PartyStatementPresentation.DetailSeparator;

    // «SarrafSettlement» هم نام PaymentKind است و هم SourceTypeِ سند تسویهٔ صراف (SourceId = شناسهٔ
    // تسویه، نه پرداخت)؛ برای جلوگیری از پیوند به پرداختِ اشتباه از این مسیر کنار گذاشته می‌شود.
    private static readonly HashSet<string> PaymentSourceTypes =
        new(Enum.GetNames<PaymentKind>().Where(name => name != nameof(PaymentKind.SarrafSettlement)), StringComparer.Ordinal);

    public async Task ApplyAsync(
        PartyRef party,
        string partyName,
        IReadOnlyList<PartyStatementRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await ApplySalesAsync(party, rows, ct);
        await ApplyLoadingsAsync(party, rows, ct);
        await ApplyPaymentsAsync(partyName, rows, ct);
        await ApplyPartySettlementsAsync(rows, ct);
        await ApplyExpensesAsync(rows, ct);
    }

    private async Task ApplySalesAsync(PartyRef party, IReadOnlyList<PartyStatementRow> rows, CancellationToken ct)
    {
        var saleRows = rows.Where(r => r.SourceType == CompanyFlowSourceTypes.Sale && r.SourceId > 0).ToList();
        if (saleRows.Count == 0)
        {
            return;
        }

        var ids = saleRows.Select(r => r.SourceId).Distinct().ToList();
        var sales = await db.SalesTransactions
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.QuantityMt,
                s.Currency,
                s.UnitPriceInCurrency,
                s.UnitPriceUsd,
                Product = s.Product != null ? (s.Product.NamePersian ?? s.Product.Name) : null,
                Customer = s.Customer != null ? (s.Customer.NamePersian ?? s.Customer.Name) : null,
                Plate = s.TruckDispatch != null && s.TruckDispatch.Truck != null ? s.TruckDispatch.Truck.PlateNumber : null
            })
            .ToDictionaryAsync(s => s.Id, ct);

        // موترهایی که خودِ دیسپچ به این فروش وصل شده‌اند (یک فروش ممکن است چند موتر داشته باشد).
        var dispatchPlates = (await db.TruckDispatches
                .AsNoTracking()
                .Where(d => d.SalesTransactionId.HasValue
                    && ids.Contains(d.SalesTransactionId.Value)
                    && d.Status != DispatchStatus.Cancelled
                    && d.Truck != null)
                .Select(d => new { SaleId = d.SalesTransactionId!.Value, d.Truck!.PlateNumber })
                .ToListAsync(ct))
            .GroupBy(d => d.SaleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlateNumber).ToList());

        foreach (var row in saleRows)
        {
            if (!sales.TryGetValue(row.SourceId, out var sale))
            {
                continue;
            }

            var verb = row.IsReversalRow ? "لغو فروش" : "فروش";
            row.Title = Join(" ", verb, sale.Product);

            var plates = new List<string?> { sale.Plate };
            if (dispatchPlates.TryGetValue(sale.Id, out var linked))
            {
                plates.AddRange(linked);
            }

            ApplyQuantityAndRate(row, sale.QuantityMt, "MT",
                (sale.UnitPriceInCurrency, sale.Currency),
                (sale.UnitPriceUsd, "USD"));
            row.Detail = JoinParts(
                Vehicles("موتر", plates),
                party.PartyType == PartyStatementPartyType.Customer ? null : Prefixed("مشتری", sale.Customer));
            row.DocumentLabel = Prefixed("فاکتور", sale.InvoiceNumber);
            row.VehicleLabel = Vehicles("موتر", plates);
        }
    }

    private async Task ApplyLoadingsAsync(PartyRef party, IReadOnlyList<PartyStatementRow> rows, CancellationToken ct)
    {
        var loadingRows = rows.Where(r => r.SourceType == CompanyFlowSourceTypes.Loading && r.SourceId > 0).ToList();
        if (loadingRows.Count == 0)
        {
            return;
        }

        var ids = loadingRows.Select(r => r.SourceId).Distinct().ToList();
        var loadings = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new
            {
                l.Id,
                l.LoadedQuantityMt,
                l.LoadingPriceUsd,
                l.SettlementUnitPriceRub,
                l.WagonNumber,
                l.BillOfLadingNumber,
                l.RwbNo,
                Product = l.Product != null ? (l.Product.NamePersian ?? l.Product.Name) : null,
                Plate = l.Truck != null ? l.Truck.PlateNumber : null,
                Vessel = l.Vessel != null ? l.Vessel.Name : null,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                Supplier = l.Contract != null && l.Contract.Supplier != null
                    ? (l.Contract.Supplier.NamePersian ?? l.Contract.Supplier.Name)
                    : null
            })
            .ToDictionaryAsync(l => l.Id, ct);

        foreach (var row in loadingRows)
        {
            if (!loadings.TryGetValue(row.SourceId, out var loading))
            {
                continue;
            }

            var verb = row.IsReversalRow ? "لغو خرید" : "خرید";
            row.Title = Join(" ", verb, loading.Product);
            ApplyQuantityAndRate(row, loading.LoadedQuantityMt, "MT",
                (loading.SettlementUnitPriceRub ?? 0m, "RUB"),
                (loading.LoadingPriceUsd ?? 0m, "USD"));
            row.Detail = JoinParts(
                Vehicles("موتر", [loading.Plate]),
                Prefixed("واگن", loading.WagonNumber),
                Prefixed("کشتی", loading.Vessel),
                party.PartyType == PartyStatementPartyType.Supplier ? null : Prefixed("از", loading.Supplier));
            row.DocumentLabel = JoinParts(
                Prefixed("قرارداد", loading.ContractNumber),
                Prefixed("بارنامه", loading.BillOfLadingNumber),
                Prefixed("RWB", loading.RwbNo));
            row.VehicleLabel = Vehicles("موتر", [loading.Plate]);
        }
    }

    private async Task ApplyPaymentsAsync(string partyName, IReadOnlyList<PartyStatementRow> rows, CancellationToken ct)
    {
        var paymentRows = rows.Where(r => PaymentSourceTypes.Contains(r.SourceType)
                && (r.LedgerEntryId.HasValue || r.SourceId > 0))
            .ToList();
        if (paymentRows.Count == 0)
        {
            return;
        }

        var ledgerIds = paymentRows.Where(r => r.LedgerEntryId.HasValue).Select(r => r.LedgerEntryId!.Value).Distinct().ToList();
        var sourceIds = paymentRows.Where(r => r.SourceId > 0).Select(r => r.SourceId).Distinct().ToList();
        var payments = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => (p.LedgerEntryId.HasValue && ledgerIds.Contains(p.LedgerEntryId.Value)) || sourceIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.LedgerEntryId,
                p.PaymentKind,
                p.Direction,
                p.Reference,
                p.Description,
                PaidByPartner = p.PaidByPartner != null ? (p.PaidByPartner.NamePersian ?? p.PaidByPartner.Name) : null,
                Counterparty = p.Customer != null ? (p.Customer.NamePersian ?? p.Customer.Name)
                    : p.Supplier != null ? (p.Supplier.NamePersian ?? p.Supplier.Name)
                    : p.ServiceProvider != null ? p.ServiceProvider.Name
                    : p.Driver != null ? p.Driver.FullName
                    : p.Sarraf != null ? p.Sarraf.Name
                    : p.Employee != null ? p.Employee.FullName
                    : null
            })
            .ToListAsync(ct);
        if (payments.Count == 0)
        {
            return;
        }

        var byLedger = payments.Where(p => p.LedgerEntryId.HasValue)
            .GroupBy(p => p.LedgerEntryId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var byId = payments.ToDictionary(p => p.Id);

        foreach (var row in paymentRows)
        {
            // پیوند قطعی: سطر دفترِ خودِ پرداخت. سطر برگشت (لغو) شناسهٔ دفتر دیگری دارد و از
            // SourceId همان پرداخت (با همان نوع) شناخته می‌شود.
            var payment = row.LedgerEntryId.HasValue && byLedger.TryGetValue(row.LedgerEntryId.Value, out var linked)
                ? linked
                : byId.TryGetValue(row.SourceId, out var bySource) && bySource.PaymentKind.ToString() == row.SourceType
                    ? bySource
                    : null;
            if (payment is null)
            {
                continue;
            }

            var name = payment.Counterparty ?? partyName;
            var isIn = payment.Direction == PaymentDirection.In;
            var core = isIn ? $"دریافت از {name}" : $"پرداخت به {name}";
            row.Title = row.IsReversalRow ? $"لغو {core}" : core;
            row.Detail = JoinParts(
                ForeignAmount(row),
                Prefixed("پرداخت توسط شریک", payment.PaidByPartner),
                PartyStatementFormatting.CleanDescription(payment.Description).NullIfEmpty());
            var reference = PartyStatementFormatting.CleanReference(row.Reference ?? payment.Reference);
            row.DocumentLabel = Prefixed(isIn ? "رسید" : "سند پرداخت", reference);
        }
    }

    private async Task ApplyPartySettlementsAsync(IReadOnlyList<PartyStatementRow> rows, CancellationToken ct)
    {
        const string sourceType = "PartySettlement";
        var settlementRows = rows.Where(r => r.SourceType == sourceType && r.SourceId > 0).ToList();
        if (settlementRows.Count == 0)
        {
            return;
        }

        var ids = settlementRows.Select(r => r.SourceId).Distinct().ToList();
        var settlements = await db.PartySettlements
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.FromPartyType,
                s.FromPartyId,
                s.ToPartyType,
                s.ToPartyId,
                s.Amount,
                s.Currency,
                s.Description
            })
            .ToDictionaryAsync(s => s.Id, ct);
        if (settlements.Count == 0)
        {
            return;
        }

        var names = await LoadAccountingPartyNamesAsync(
            settlements.Values.SelectMany(s => new[] { (s.FromPartyType, s.FromPartyId), (s.ToPartyType, s.ToPartyId) }),
            ct);

        foreach (var row in settlementRows)
        {
            if (!settlements.TryGetValue(row.SourceId, out var s))
            {
                continue;
            }

            var from = names.TryGetValue((s.FromPartyType, s.FromPartyId), out var fromName) ? fromName : $"#{s.FromPartyId}";
            var to = names.TryGetValue((s.ToPartyType, s.ToPartyId), out var toName) ? toName : $"#{s.ToPartyId}";
            var amount = $"{PartyStatementPresentation.Money(s.Amount)} {NormalizeCurrency(s.Currency)}";
            row.Title = row.IsReversalRow
                ? $"لغو پرداخت مستقیم {amount} از حساب {from} به {to}"
                : $"{amount} از حساب {from}، مستقیم به {to} پرداخت شد";
            row.Detail = PartyStatementFormatting.CleanDescription(s.Description).NullIfEmpty();
            row.DocumentLabel = Prefixed("سند تسویه", $"PS-{s.Id}");
        }
    }

    private async Task ApplyExpensesAsync(IReadOnlyList<PartyStatementRow> rows, CancellationToken ct)
    {
        var expenseRows = rows.Where(r => r.SourceType == CompanyFlowSourceTypes.Expense && r.SourceId > 0).ToList();
        if (expenseRows.Count == 0)
        {
            return;
        }

        var ids = expenseRows.Select(r => r.SourceId).Distinct().ToList();
        var expenses = await db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                TypeName = e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : null,
                DispatchPlate = e.TruckDispatch != null && e.TruckDispatch.Truck != null ? e.TruckDispatch.Truck.PlateNumber : null,
                LoadingPlate = e.LoadingRegister != null && e.LoadingRegister.Truck != null ? e.LoadingRegister.Truck.PlateNumber : null,
                Wagon = e.LoadingRegister != null ? e.LoadingRegister.WagonNumber : null
            })
            .ToDictionaryAsync(e => e.Id, ct);

        foreach (var row in expenseRows)
        {
            if (!expenses.TryGetValue(row.SourceId, out var expense) || string.IsNullOrWhiteSpace(expense.TypeName))
            {
                continue;
            }

            row.Title = row.IsReversalRow ? $"لغو {expense.TypeName}" : expense.TypeName;
            row.Detail = JoinParts(
                ForeignAmount(row),
                Vehicles("موتر", [expense.DispatchPlate, expense.LoadingPlate]),
                Prefixed("واگن", expense.Wagon));
            row.DocumentLabel = PartyStatementFormatting.CleanReference(row.Reference);
        }
    }

    private async Task<Dictionary<(AccountingPartyType, int), string>> LoadAccountingPartyNamesAsync(
        IEnumerable<(AccountingPartyType Type, int Id)> keys,
        CancellationToken ct)
    {
        var list = keys.Distinct().ToList();
        int[] Ids(AccountingPartyType type) => list.Where(k => k.Type == type).Select(k => k.Id).ToArray();
        var result = new Dictionary<(AccountingPartyType, int), string>();

        var customerIds = Ids(AccountingPartyType.Customer);
        if (customerIds.Length > 0)
        {
            foreach (var p in await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                         .Select(c => new { c.Id, Name = c.NamePersian ?? c.Name }).ToListAsync(ct))
                result[(AccountingPartyType.Customer, p.Id)] = p.Name;
        }

        var supplierIds = Ids(AccountingPartyType.Supplier);
        if (supplierIds.Length > 0)
        {
            foreach (var p in await db.Suppliers.AsNoTracking().Where(c => supplierIds.Contains(c.Id))
                         .Select(c => new { c.Id, Name = c.NamePersian ?? c.Name }).ToListAsync(ct))
                result[(AccountingPartyType.Supplier, p.Id)] = p.Name;
        }

        var providerIds = Ids(AccountingPartyType.ServiceProvider);
        if (providerIds.Length > 0)
        {
            foreach (var p in await db.ServiceProviders.AsNoTracking().Where(c => providerIds.Contains(c.Id))
                         .Select(c => new { c.Id, c.Name }).ToListAsync(ct))
                result[(AccountingPartyType.ServiceProvider, p.Id)] = p.Name;
        }

        var driverIds = Ids(AccountingPartyType.Driver);
        if (driverIds.Length > 0)
        {
            foreach (var p in await db.Drivers.AsNoTracking().Where(c => driverIds.Contains(c.Id))
                         .Select(c => new { c.Id, c.FullName }).ToListAsync(ct))
                result[(AccountingPartyType.Driver, p.Id)] = p.FullName;
        }

        return result;
    }

    /// <summary>
    /// مقدار و نرخ فی واحد را در فیلدهای جداگانهٔ سطر می‌نشاند. هر نرخ کاندید فقط وقتی پذیرفته
    /// می‌شود که مقدار × نرخ با مبلغ همان سطر (به ارز اصلی سطر یا به USD) برابر باشد؛ وگرنه
    /// نرخ خالی می‌ماند.
    /// </summary>
    private static void ApplyQuantityAndRate(
        PartyStatementRow row,
        decimal quantity,
        string unit,
        params (decimal Rate, string? Currency)[] candidates)
    {
        if (quantity <= 0m)
        {
            return;
        }

        row.TradeQuantity = quantity;
        row.TradeQuantityUnit = unit;
        var usdAmount = row.ReceiptBase ?? row.OutflowBase;
        foreach (var (rate, rawCurrency) in candidates)
        {
            if (rate <= 0m)
            {
                continue;
            }

            var currency = NormalizeCurrency(rawCurrency);
            decimal? target = string.Equals(currency, row.OriginalCurrency, StringComparison.OrdinalIgnoreCase)
                ? row.OriginalAmount
                : currency == "USD" ? usdAmount : null;
            if (!target.HasValue || !Matches(quantity * rate, Math.Abs(target.Value)))
            {
                continue;
            }

            row.TradeUnitPrice = rate;
            row.TradeUnitPriceCurrency = currency;
            return;
        }
    }

    private static bool Matches(decimal computed, decimal actual)
        => Math.Abs(computed - actual) <= Math.Max(0.05m, actual * 0.0001m);

    // مبلغ به ارز اصلی سند وقتی با ارز صورت‌حساب فرق دارد: «۵۰٬۰۰۰.۰۰ AFN • 1 USD = 70 AFN».
    private static string? ForeignAmount(PartyStatementRow row)
    {
        if (!row.OriginalAmount.HasValue
            || string.Equals(row.OriginalCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return JoinParts(
            $"{PartyStatementPresentation.Money(Math.Abs(row.OriginalAmount.Value))} {row.OriginalCurrency}",
            row.FxRateDisplay);
    }

    private static string? Vehicles(string label, IEnumerable<string?> plates)
    {
        var distinct = plates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? null : $"{label} {string.Join("، ", distinct)}";
    }

    private static string? Prefixed(string label, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : $"{label} {value.Trim()}";

    private static string Join(string separator, params string?[] parts)
        => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static string? JoinParts(params string?[] parts)
    {
        var text = Join(Separator, parts);
        return text.Length == 0 ? null : text;
    }

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
}

internal static class PartyStatementNarrativeText
{
    public static string? NullIfEmpty(this string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
