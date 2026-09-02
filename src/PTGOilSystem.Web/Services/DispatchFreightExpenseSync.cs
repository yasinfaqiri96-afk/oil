using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services;

// منطق واحدِ همگام‌سازی «کرایه دیسپچ موتر» با ExpenseTransaction + LedgerEntry.
// از DispatchController استخراج شده تا فرم جدید «رسید/تسویه/تخلیه وسایط» همان رکوردها را
// بسازد/به‌روز کند و هیچ منطق مالی موازی به‌وجود نیاید. رفتار عیناً حفظ شده است:
// یک expense فعال per dispatch (نوع TRUCK-DISPATCH-FREIGHT) + ledger با کلید (SourceType=Expense, SourceId).
public static class DispatchFreightExpenseSync
{
    public const string DispatchFreightExpenseCode = "TRUCK-DISPATCH-FREIGHT";

    // مرحله ۵ — Dual-write اختیاری. کلاس static است، پس Adapter به‌جای تزریق، پارامتر اختیاری
    // است؛ اگر پاس داده نشود مسیر قدیمی هیچ تغییری نمی‌کند.
    public static async Task SyncAsync(
        ApplicationDbContext db,
        TruckDispatch dispatch,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        // دیسپچِ وصل‌به‌رسید (مثل انتقال گروهی واگن→موتر) هم می‌تواند کرایه داشته باشد.
        // فقط وقتی اینجا چیزی نمی‌سازیم که خودِ رسید کرایه دارد؛ آن کرایه جداگانه به‌عنوان
        // TRANSPORT-RECEIPT-FREIGHT ثبت شده و ساختنِ کرایهٔ دیسپچ باعث دوباره‌شماری می‌شود.
        // رسیدِ DirectDispatch کرایه ندارد (کرایه روی خود دیسپچ تسویه می‌شود) ⇒ باید مصرف بسازیم.
        if (dispatch.InventoryTransportReceiptId.HasValue)
        {
            var receiptFreight = await db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => r.Id == dispatch.InventoryTransportReceiptId.Value)
                .Select(r => new { r.FreightPayableUsd, r.FreightCostUsd })
                .FirstOrDefaultAsync();
            if (receiptFreight is not null
                && GetFreightExpenseAmountUsd(receiptFreight.FreightPayableUsd, receiptFreight.FreightCostUsd) > 0m)
            {
                return;
            }
        }

        var existingExpenses = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Where(e => e.TruckDispatchId == dispatch.Id
                && e.ExpenseType != null
                && e.ExpenseType.Code == DispatchFreightExpenseCode
                && !e.IsCancelled)
            .OrderByDescending(e => e.Id)
            .ToListAsync();

        var primaryExpense = existingExpenses.FirstOrDefault();
        foreach (var duplicate in existingExpenses.Skip(1))
        {
            await CancelExpenseAsync(db, duplicate);
        }

        // طرفِ کرایه: شرکت خدماتی یا (اگر انتخاب نشد) موتروانِ مستقل. موترِ خودِ شرکت (دارایی عملیاتی)
        // کرایه نمی‌گیرد؛ پس هیچ مصرف/لجرِ کرایه‌ای ساخته نمی‌شود.
        var serviceProviderId = dispatch.ServiceProviderId;
        var driverId = serviceProviderId.HasValue ? (int?)null : dispatch.DriverId;
        var amountUsd = GetFreightExpenseAmountUsd(dispatch.FreightPayableUsd, dispatch.FreightCostUsd);
        if (dispatch.OperationalAssetId.HasValue
            || (!serviceProviderId.HasValue && !driverId.HasValue)
            || amountUsd <= 0m)
        {
            if (primaryExpense is not null)
            {
                await CancelExpenseAsync(db, primaryExpense);
            }

            return;
        }

        var expenseType = await EnsureExpenseTypeAsync(db);
        var description = $"Truck dispatch freight for dispatch #{dispatch.Id}";

        if (primaryExpense is null)
        {
            primaryExpense = new ExpenseTransaction
            {
                ExpenseTypeId = expenseType.Id,
                ContractId = dispatch.ContractId,
                TruckDispatchId = dispatch.Id,
                ServiceProviderId = serviceProviderId,
                DriverId = driverId,
                ExpenseDate = dispatch.DispatchDate.Date,
                Amount = amountUsd,
                Currency = SystemCurrency.BaseCurrencyCode,
                AppliedFxRateToUsd = 1m,
                AmountUsd = amountUsd,
                Description = description
            };

            db.ExpenseTransactions.Add(primaryExpense);
            await db.SaveChangesAsync();
        }
        else
        {
            primaryExpense.ExpenseTypeId = expenseType.Id;
            primaryExpense.ContractId = dispatch.ContractId;
            primaryExpense.TruckDispatchId = dispatch.Id;
            primaryExpense.ServiceProviderId = serviceProviderId;
            primaryExpense.OperationalAssetId = null;
            primaryExpense.DriverId = driverId;
            primaryExpense.ExpenseDate = dispatch.DispatchDate.Date;
            primaryExpense.Amount = amountUsd;
            primaryExpense.Currency = SystemCurrency.BaseCurrencyCode;
            primaryExpense.AppliedFxRateToUsd = 1m;
            primaryExpense.AmountUsd = amountUsd;
            primaryExpense.Description = description;
            primaryExpense.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await UpsertLedgerAsync(db, primaryExpense);

        // مرحله ۵ — Dual-write داخل همان Transaction قدیمی. برای مسیر به‌روزرسانی هم صدا زده
        // می‌شود: Adapter مبلغِ تغییرنکرده را Duplicate می‌گیرد و بی‌اثر است.
        if (expenseAccounting is not null)
        {
            await expenseAccounting.TryPostExpenseAsync(primaryExpense);
        }
    }

    public static async Task CancelByDispatchIdAsync(
        ApplicationDbContext db,
        int dispatchId,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        var expenses = await db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Where(e => e.TruckDispatchId == dispatchId
                && e.ExpenseType != null
                && e.ExpenseType.Code == DispatchFreightExpenseCode
                && !e.IsCancelled)
            .ToListAsync();

        foreach (var expense in expenses)
        {
            await CancelExpenseAsync(db, expense, expenseAccounting);
        }
    }

    public static async Task CancelExpenseAsync(
        ApplicationDbContext db,
        ExpenseTransaction expense,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        DateTime? reversalDate = null)
    {
        if (expense.IsCancelled)
        {
            return;
        }

        // مرحله ۵ — Reversal قبل از علامت‌خوردن IsCancelled صدا زده می‌شود تا Adapter بتواند
        // شرکت را از همان روابط قبلی حل کند. Idempotent است.
        if (expenseAccounting is not null)
        {
            await expenseAccounting.TryPostExpenseReversalAsync(expense);
        }

        expense.IsCancelled = true;
        expense.UpdatedAtUtc = DateTime.UtcNow;
        var ledger = await db.LedgerEntries
            .Where(l => l.SourceType == "Expense"
                && l.SourceId == expense.Id
                && (l.Reference == null || !l.Reference.EndsWith(LedgerReversalWriter.CancelReferenceSuffix)))
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();
        if (ledger is not null)
        {
            await LedgerReversalWriter.ReverseAsync(
                db,
                ledger,
                reversalDate ?? DateTime.UtcNow.Date,
                $"لغو کرایه/مصرف #{expense.Id} | {ledger.Description}",
                $"EXP-{expense.Id}");
        }

        await db.SaveChangesAsync();
    }

    private static async Task UpsertLedgerAsync(ApplicationDbContext db, ExpenseTransaction expense)
    {
        var ledger = await db.LedgerEntries
            .Where(l => l.SourceType == "Expense"
                && l.SourceId == expense.Id
                && (l.Reference == null || !l.Reference.EndsWith(LedgerReversalWriter.CancelReferenceSuffix)))
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        var request = new LedgerPostingRequest
        {
            SourceType = "Expense",
            SourceId = expense.Id,
            EntryDate = expense.ExpenseDate,
            // کرایه بدهیِ ما به حمل‌کننده است ⇒ Credit روی حساب همان طرف (شرکت خدماتی یا راننده).
            Side = LedgerSide.Credit,
            AmountUsd = expense.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = expense.Amount,
            SourceCurrencyCode = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AppliedFxRateDate = expense.ExpenseDate,
            AppliedFxRateSource = "Base currency",
            Description = expense.Description ?? "Truck dispatch freight",
            Reference = $"TRUCK-DISPATCH:{expense.TruckDispatchId}",
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            ServiceProviderId = expense.ServiceProviderId,
            DriverId = expense.DriverId,

            // فیلدهایی که این هماهنگ‌سازی هرگز دست نمی‌زد، عیناً از سطر موجود می‌آیند
            // تا خروجی دقیقاً همان چیزی بماند که پیش از تمرکز نوشته می‌شد (PTG-P1-03).
            AppliedCurrencyPerUsdRate = ledger?.AppliedCurrencyPerUsdRate,
            ViaSarrafGroupId = ledger?.ViaSarrafGroupId,
            CustomerId = ledger?.CustomerId,
            SupplierId = ledger?.SupplierId,
            EmployeeId = ledger?.EmployeeId,
        };

        var posting = new LedgerPostingService(db);
        if (ledger is null)
        {
            posting.Post(request);
        }
        else
        {
            posting.Apply(ledger, request);
        }

        await db.SaveChangesAsync();
    }

    private static async Task<ExpenseType> EnsureExpenseTypeAsync(ApplicationDbContext db)
    {
        var expenseType = await db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == DispatchFreightExpenseCode);
        if (expenseType is not null)
        {
            if (!expenseType.IsActive)
            {
                expenseType.IsActive = true;
                await db.SaveChangesAsync();
            }

            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = DispatchFreightExpenseCode,
            Name = "Truck Dispatch Freight",
            NamePersian = "کرایه دیسپچ موتر",
            Category = "Transport",
            IsActive = true
        };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();
        return expenseType;
    }

    public static decimal GetFreightExpenseAmountUsd(decimal? payableUsd, decimal? grossUsd)
        => payableUsd.HasValue && payableUsd.Value > 0m
            ? payableUsd.Value
            : grossUsd.HasValue && grossUsd.Value > 0m
                ? grossUsd.Value
                : 0m;
}
