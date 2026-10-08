using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services.Customs;

/// <summary>مبلغِ سندِ نقدیِ یک گروهِ گمرک به ارزِ صندوق، با نرخِ تبدیل به USD.</summary>
public sealed record CustomsCashAmount(decimal Amount, string Currency, decimal RateToUsd);

/// <summary>
/// منطقِ واحدِ همگام‌سازی «هزینهٔ اظهارنامهٔ گمرکی» با <see cref="ExpenseTransaction"/> و
/// <see cref="LedgerEntry"/>.
///
/// پیش از این، هزینهٔ گمرک یک انبارِ مالیِ موازی بود: گزارش سود و زیان مبلغ را مستقیم از
/// <c>CustomsDeclarations.TotalUsd</c> می‌خواند و هیچ سطرِ دفتری وجود نداشت — پس همان پول
/// در دفتر کل دیده نمی‌شد، در حساب هیچ طرف‌حسابی نمی‌نشست، و اگر کسی همان هزینه را از فرم
/// «مصارف گمرکی» هم ثبت می‌کرد، در P&amp;L دو بار شمرده می‌شد.
///
/// یک اظهارنامه به‌اندازهٔ دو گروهِ <see cref="CustomsComponentGroup"/> مصرف می‌سازد، چون
/// جنسِ پولشان یکی نیست: حقوق دولتی همان‌جا نقد می‌شود و کمیشنکار اغلب بدهی می‌ماند.
///
/// idempotent است: هر گروه حداکثر یک مصرفِ فعال دارد و فراخوانیِ دوباره همان ردیف را
/// به‌روز می‌کند، نه اینکه ردیفِ دوم بسازد. مبلغِ صفر یعنی مصرفِ آن گروه باید لغو شود.
/// </summary>
public static class CustomsDeclarationExpenseSync
{
    public const string DutyExpenseCode = "CUSTOMS-DUTY";
    public const string ServiceExpenseCode = "CUSTOMS-SERVICE";
    public const string CashCurrencyMismatchCode = "CUSTOMS_CASH_CURRENCY_AMOUNT_UNKNOWN";

    // PTG-P1-04 — قاعدهٔ «هر مصرف دقیقاً یک هویت تسویه دارد». بی‌حالت است.
    private static readonly IExpenseSettlementValidator SettlementValidator = new ExpenseSettlementValidator();

    /// <summary>
    /// مبلغِ USD هر گروه از اقلامِ همان اظهارنامه. مبنا همان ستونی است که خودِ اظهارنامه
    /// برای <c>TotalUsd</c> جمع می‌زند، پس جمعِ دو گروه با کلِ اظهارنامه یکی می‌ماند.
    /// </summary>
    public static (decimal DutyUsd, decimal ServiceUsd) SplitUsd(IEnumerable<CustomsDeclarationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var duty = 0m;
        var service = 0m;
        foreach (var item in items)
        {
            var amountUsd = item.AmountUsd ?? 0m;
            if (amountUsd <= 0m)
            {
                continue;
            }

            if (CustomsComponentGroupMap.Resolve(item.ComponentType) == CustomsComponentGroup.GovernmentDuty)
            {
                duty += amountUsd;
            }
            else
            {
                service += amountUsd;
            }
        }

        return (duty, service);
    }

    /// <summary>
    /// مبلغِ AFN یک گروه از همان اقلامی که <see cref="SplitUsd"/> برای آن گروه می‌شمارد. اگر یکی از
    /// آن اقلام معادل AFN نداشته باشد (ردیف دالریِ بدون نرخ)، مبلغِ افغانیِ گروه معلوم نیست ⇒ null.
    /// </summary>
    public static decimal? GroupAfn(IEnumerable<CustomsDeclarationItem> items, CustomsComponentGroup group)
    {
        ArgumentNullException.ThrowIfNull(items);

        var total = 0m;
        foreach (var item in items)
        {
            if ((item.AmountUsd ?? 0m) <= 0m || CustomsComponentGroupMap.Resolve(item.ComponentType) != group)
            {
                continue;
            }

            if (item.AmountAfn <= 0m)
            {
                return null;
            }

            total += item.AmountAfn;
        }

        return total;
    }

    /// <summary>
    /// سندِ «نقد پرداخت شد» به ارزِ همان صندوق ثبت می‌شود تا صندوق به ارزِ خودش کم شود. صندوقِ
    /// دالری ⇒ USD با نرخ ۱. صندوقِ افغانی ⇒ جمعِ AFN اقلامِ گروه، و نرخ = USD ÷ AFN، تا معادلِ دالری
    /// عیناً همان معادلِ ثبت‌شدهٔ اظهارنامه بماند و AmountUsd = round(Amount × نرخ, 4) برقرار باشد
    /// (همان شرطی که ExpenseAccountingAdapter و AccountingPostingService می‌خواهند).
    /// null ⇒ سندِ درست ساخته نمی‌شود: ارزِ دیگر، مبلغ AFN نامعلوم، یا نرخی که USD را دقیق نمی‌سازد.
    /// </summary>
    public static CustomsCashAmount? ResolveCashAmount(string? cashAccountCurrency, decimal amountUsd, decimal? amountAfn)
    {
        var currency = SystemCurrency.Normalize(cashAccountCurrency);
        if (SystemCurrency.IsBaseCurrency(currency))
        {
            return new CustomsCashAmount(amountUsd, SystemCurrency.BaseCurrencyCode, 1m);
        }

        if (currency != Models.Customs.CustomsCurrency.Afn || amountAfn is not > 0m || amountUsd <= 0m)
        {
            return null;
        }

        var rate = FxRateMath.RoundRate(amountUsd / amountAfn.Value);
        return decimal.Round(amountAfn.Value * rate, 4, MidpointRounding.AwayFromZero) == amountUsd
            ? new CustomsCashAmount(amountAfn.Value, currency, rate)
            : null;
    }

    public static async Task SyncAsync(
        ApplicationDbContext db,
        CustomsDeclaration declaration,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(declaration);

        var items = declaration.Items?.Count > 0
            ? declaration.Items.ToList()
            : await db.CustomsDeclarationItems
                .Where(i => i.CustomsDeclarationId == declaration.Id)
                .ToListAsync(ct);

        var (dutyUsd, serviceUsd) = SplitUsd(items);

        await SyncGroupAsync(
            db,
            declaration,
            items,
            CustomsComponentGroup.GovernmentDuty,
            dutyUsd,
            declaration.DutySettlementMode,
            declaration.DutyCashAccountId,
            declaration.DutyServiceProviderId,
            expenseAccounting,
            ct);

        await SyncGroupAsync(
            db,
            declaration,
            items,
            CustomsComponentGroup.ThirdPartyService,
            serviceUsd,
            declaration.ServiceSettlementMode,
            declaration.ServiceCashAccountId,
            declaration.ServiceProviderId,
            expenseAccounting,
            ct);
    }

    private static async Task SyncGroupAsync(
        ApplicationDbContext db,
        CustomsDeclaration declaration,
        IReadOnlyCollection<CustomsDeclarationItem> items,
        CustomsComponentGroup group,
        decimal amountUsd,
        ExpenseSettlementMode settlementMode,
        int? cashAccountId,
        int? serviceProviderId,
        Accounting.IExpenseAccountingAdapter? expenseAccounting,
        CancellationToken ct)
    {
        var existing = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declaration.Id
                && e.CustomsComponentGroup == group
                && !e.IsCancelled)
            .OrderByDescending(e => e.Id)
            .ToListAsync(ct);

        var primary = existing.FirstOrDefault();
        foreach (var duplicate in existing.Skip(1))
        {
            await CancelAsync(db, duplicate, expenseAccounting, ct);
        }

        // گروهی که مبلغ ندارد نباید مصرف داشته باشد — و اگر قبلاً داشت، همان باید لغو شود.
        // ردیفِ «طبقه‌بندی‌نشده» هم عمداً مصرف نمی‌سازد: هویتِ تسویه حدس زده نمی‌شود.
        if (amountUsd <= 0m || settlementMode == ExpenseSettlementMode.Unknown)
        {
            if (primary is not null)
            {
                await CancelAsync(db, primary, expenseAccounting, ct);
            }

            return;
        }

        // اظهارنامه هر دو ستونِ AFN/USD را به‌عنوان «معادلِ همان مبلغ» ذخیره می‌کند. سندِ نقدی به ارزِ
        // صندوقِ انتخاب‌شده نوشته می‌شود؛ بقیهٔ حالت‌ها مثل قبل روی USD بسته می‌شوند.
        var cashAmount = new CustomsCashAmount(amountUsd, SystemCurrency.BaseCurrencyCode, 1m);
        if (settlementMode == ExpenseSettlementMode.PaidImmediately && cashAccountId is > 0)
        {
            var cashCurrency = await db.CashAccounts
                .Where(a => a.Id == cashAccountId.Value)
                .Select(a => a.Currency)
                .FirstOrDefaultAsync(ct);
            cashAmount = ResolveCashAmount(cashCurrency, amountUsd, GroupAfn(items, group))
                ?? throw new BusinessRuleException(
                    CashCurrencyMismatchCode,
                    $"سند نقدی {CustomsComponentGroupMap.Label(group)} گمرک به ارز صندوق ({SystemCurrency.Normalize(cashCurrency)}) ساخته نمی‌شود؛ معادل افغانی و نرخ همهٔ ردیف‌ها لازم است.");
        }

        var expenseType = await EnsureExpenseTypeAsync(db, group, ct);
        var description = BuildDescription(declaration, group);

        if (primary is null)
        {
            primary = new ExpenseTransaction
            {
                CustomsDeclarationId = declaration.Id,
                CustomsComponentGroup = group
            };
            db.ExpenseTransactions.Add(primary);
        }

        primary.ExpenseTypeId = expenseType.Id;
        // ارجاعاتِ عملیاتی عیناً از خودِ اظهارنامه می‌آیند تا هزینه به همان پروندهٔ محموله
        // بچسبد که گزارش‌ها از آن می‌خوانند.
        primary.TransportLegId = declaration.TransportLegId;
        primary.TruckDispatchId = declaration.TruckDispatchId;
        primary.LoadingRegisterId = declaration.LoadingRegisterId;
        primary.ContractId = await ResolveContractIdAsync(db, declaration, ct);
        primary.ExpenseDate = declaration.DeclarationDate.Date;
        primary.Amount = cashAmount.Amount;
        primary.Currency = cashAmount.Currency;
        primary.AppliedFxRateToUsd = cashAmount.RateToUsd;
        primary.AmountUsd = amountUsd;
        primary.Description = description;
        primary.ServiceProviderId = settlementMode == ExpenseSettlementMode.Payable ? serviceProviderId : null;
        primary.UpdatedAtUtc = DateTime.UtcNow;

        switch (settlementMode)
        {
            case ExpenseSettlementMode.PaidImmediately:
                primary.SettlementMode = ExpenseSettlementMode.PaidImmediately;
                primary.CounterpartyType = null;
                primary.CounterpartyId = null;
                primary.CashAccountId = cashAccountId;
                break;

            case ExpenseSettlementMode.NonCash:
                primary.SettlementMode = ExpenseSettlementMode.NonCash;
                primary.CounterpartyType = null;
                primary.CounterpartyId = null;
                primary.CashAccountId = null;
                break;

            default:
                // Payable — طرف‌حساب از همان ServiceProviderId بالا آمد، پس قاعدهٔ واحدِ
                // ExpenseLedgerPoster همان را می‌خواند و نسخهٔ دومی از قاعده ساخته نمی‌شود.
                primary.CashAccountId = null;
                ExpenseLedgerPoster.ApplyCounterpartySettlement(primary);

                // کاربر گفته «بدهی ماند». اگر طرف‌حسابی انتخاب نشده باشد، قاعدهٔ بالا
                // به‌درستی چیزی برای بدهکارشدن پیدا نمی‌کند و NonCash می‌دهد — ولی سکوت
                // یعنی تبدیلِ خاموشِ یک بدهی به «بدون حرکت پول». پس همان چیزی که کاربر
                // گفت نوشته می‌شود و اعتبارسنجِ پایین سند را رد می‌کند.
                primary.SettlementMode = ExpenseSettlementMode.Payable;
                break;
        }

        SettlementValidator.Validate(primary);
        await db.SaveChangesAsync(ct);

        await UpsertLedgerAsync(db, primary, expenseType, ct);

        if (expenseAccounting is not null)
        {
            await expenseAccounting.TryPostExpenseAsync(primary, ct);
        }
    }

    private static async Task UpsertLedgerAsync(
        ApplicationDbContext db,
        ExpenseTransaction expense,
        ExpenseType expenseType,
        CancellationToken ct)
    {
        var poster = new ExpenseLedgerPoster(new LedgerPostingService(db));
        var request = new ExpenseLedgerRequest
        {
            Expense = expense,
            ExpenseType = expenseType,
            Description = expense.Description ?? "Customs",
            Reference = $"CUSTOMS-DECLARATION:{expense.CustomsDeclarationId}-{(int)expense.CustomsComponentGroup!.Value}",
            FxRateSource = SystemCurrency.IsBaseCurrency(expense.Currency)
                ? "Base currency"
                : "Customs declaration item rates"
        };

        var existing = await db.LedgerEntries
            .FirstOrDefaultAsync(
                l => l.SourceType == ExpenseLedgerPoster.ExpenseSourceType && l.SourceId == expense.Id,
                ct);

        if (existing is null)
        {
            poster.Post(request);
        }
        else
        {
            poster.Apply(existing, request);
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task CancelByDeclarationIdAsync(
        ApplicationDbContext db,
        int declarationId,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        CancellationToken ct = default)
    {
        var expenses = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declarationId && !e.IsCancelled)
            .ToListAsync(ct);

        foreach (var expense in expenses)
        {
            await CancelAsync(db, expense, expenseAccounting, ct);
        }
    }

    private static async Task CancelAsync(
        ApplicationDbContext db,
        ExpenseTransaction expense,
        Accounting.IExpenseAccountingAdapter? expenseAccounting,
        CancellationToken ct)
    {
        if (expense.IsCancelled)
        {
            return;
        }

        // Reversal پیش از علامت‌خوردنِ IsCancelled صدا زده می‌شود تا Adapter بتواند شرکت را
        // از همان روابطِ قبلی حل کند — همان ترتیبی که DispatchFreightExpenseSync دارد.
        if (expenseAccounting is not null)
        {
            await expenseAccounting.TryPostExpenseReversalAsync(expense, ct);
        }

        var ledgers = await db.LedgerEntries
            .Where(l => l.SourceType == ExpenseLedgerPoster.ExpenseSourceType && l.SourceId == expense.Id)
            .ToListAsync(ct);
        db.LedgerEntries.RemoveRange(ledgers);

        expense.IsCancelled = true;
        expense.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// قرارداد از همان مسیرِ عملیاتیِ اظهارنامه خوانده می‌شود؛ اظهارنامه ستونِ قرارداد ندارد.
    /// </summary>
    private static async Task<int?> ResolveContractIdAsync(
        ApplicationDbContext db,
        CustomsDeclaration declaration,
        CancellationToken ct)
    {
        if (declaration.TransportLegId.HasValue)
        {
            return await db.InventoryTransportLegs
                .Where(l => l.Id == declaration.TransportLegId.Value)
                .Select(l => (int?)l.SourcePurchaseContractId)
                .FirstOrDefaultAsync(ct);
        }

        if (declaration.TruckDispatchId.HasValue)
        {
            return await db.TruckDispatches
                .Where(d => d.Id == declaration.TruckDispatchId.Value)
                .Select(d => (int?)d.ContractId)
                .FirstOrDefaultAsync(ct);
        }

        if (declaration.LoadingRegisterId.HasValue)
        {
            return await db.LoadingRegisters
                .Where(r => r.Id == declaration.LoadingRegisterId.Value)
                .Select(r => r.ContractId)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    private static string BuildDescription(CustomsDeclaration declaration, CustomsComponentGroup group)
    {
        var vehicle = string.IsNullOrWhiteSpace(declaration.WagonOrTruckNumber)
            ? $"اظهارنامه #{declaration.Id}"
            : $"{declaration.WagonOrTruckNumber} — اظهارنامه #{declaration.Id}";
        return $"{CustomsComponentGroupMap.Label(group)} گمرک | {vehicle}";
    }

    private static async Task<ExpenseType> EnsureExpenseTypeAsync(
        ApplicationDbContext db,
        CustomsComponentGroup group,
        CancellationToken ct)
    {
        var code = group == CustomsComponentGroup.GovernmentDuty ? DutyExpenseCode : ServiceExpenseCode;
        var existing = await db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == code, ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ExpenseType
        {
            Code = code,
            Name = group == CustomsComponentGroup.GovernmentDuty ? "Customs Duty" : "Customs Service Fees",
            NamePersian = group == CustomsComponentGroup.GovernmentDuty ? "حقوق گمرکی" : "کمیشن و خدمات گمرکی",
            Category = "Customs",
            IsActive = true
        };
        db.ExpenseTypes.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }
}
