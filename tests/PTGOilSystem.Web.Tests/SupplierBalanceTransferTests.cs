using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «مانده قابل انتقال» تأمین‌کننده و انتقال آن به قرارداد.
///
/// قرارداد مالی که این تست‌ها قفل می‌کنند:
///   طلب خالص  = Σبرد − Σرسید  (مثبت یعنی شرکت طلبکار)
///   قابل انتقال = طلب خالص − تخصیص‌های قدیمی فعال − انتقال‌های جدید فعال
///   انتقال هرگز مانده کلی تأمین‌کننده و حساب بانک/صندوق را تغییر نمی‌دهد.
/// </summary>
public class SupplierBalanceTransferTests
{
    private const int SupplierId = 1;
    private const int OtherSupplierId = 2;
    private const int CompanyId1 = 1;
    private const int CompanyId2 = 2;
    private const int RubContractId = 1;      // قرارداد روبلی تأمین‌کننده ۱
    private const int UsdContractId = 4;      // قرارداد دالری تأمین‌کننده ۱
    private const int UsdContract2Id = 5;     // قرارداد دالری دوم تأمین‌کننده ۱
    private const int OtherSupplierContractId = 3;
    private const int OtherCompanyContractId = 6;

    // ---------- ۱ تا ۶: منابع مختلف ایجاد طلب ----------

    // ۱) طلب روبلی از پرداخت مستقیم.
    [Fact]
    public async Task Claim_From_Direct_Rub_Payment_Is_Transferable_In_Rub()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 111111.1111m, "RUB", 10_000_000m, 0.0111111m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(111111.1111m, Company1(balance).ClaimUsd);
        var bucket = Assert.Single(Company1(balance).Buckets);
        Assert.Equal("RUB", bucket.CurrencyCode);
        Assert.Equal(10_000_000m, bucket.RemainingOriginalAmount);
    }

    // ۲) طلب روبلی از پرداخت از راه صراف.
    [Fact]
    public async Task Claim_From_Sarraf_Payment_Is_Transferable()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierViaSarrafPayment", LedgerSide.Debit, 5000m, "RUB", 400_000m, 0.0125m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(5000m, Company1(balance).TransferableTotalUsd);
        Assert.Equal(400_000m, Assert.Single(Company1(balance).Buckets).RemainingOriginalAmount);
    }

    // ۳) طلب دالری.
    [Fact]
    public async Task Claim_From_Usd_Payment_Is_Transferable_In_Usd()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 50_000m, "USD", 50_000m, 1m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        var bucket = Assert.Single(Company1(balance).Buckets);
        Assert.Equal("USD", bucket.CurrencyCode);
        Assert.Equal(50_000m, bucket.RemainingOriginalAmount);
        Assert.Equal(1m, bucket.WeightedHistoricalFxRateToUsd);
    }

    // ۴) مانده اول دوره.
    [Fact]
    public async Task Claim_From_Opening_Balance_Is_Transferable()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "OpeningBalance", LedgerSide.Debit, 3000m, "RUB", 240_000m, 0.0125m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(3000m, Company1(balance).TransferableTotalUsd);
        Assert.Equal("OpeningBalance", Assert.Single(Assert.Single(Company1(balance).Buckets).Slices).SourceType);
    }

    // ۵) اصلاح حساب.
    [Fact]
    public async Task Claim_From_Manual_Adjustment_Is_Transferable()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "ManualAdjustment", LedgerSide.Debit, 1200m, "USD", 1200m, 1m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(1200m, Company1(balance).TransferableTotalUsd);
    }

    // ۶) اضافه‌پرداخت قرارداد: پرداخت ۱۰۰k روی قرارداد و بارگیری ۸۰k → فقط ۲۰k قابل انتقال.
    [Fact]
    public async Task Contract_Overpayment_Leaves_Only_Surplus_Transferable()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 100_000m, "USD", 100_000m, 1m, contractId: UsdContractId);
        AddSupplierLedger(db, "Loading", LedgerSide.Credit, 80_000m, "USD", 80_000m, 1m, contractId: UsdContractId);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(20_000m, Company1(balance).ClaimUsd);
        Assert.Equal(20_000m, Company1(balance).TransferableTotalUsd);
        Assert.Equal(20_000m, Assert.Single(Company1(balance).Buckets).RemainingOriginalAmount);
    }

    // ---------- ۷ تا ۱۲: خودِ انتقال ----------

    // ۷) انتقال به یک قرارداد.
    [Fact]
    public async Task Transfer_To_Single_Contract_Locks_Amounts()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 90m, (UsdContractId, 2_000_000m, 1m)));

        var transfer = Assert.Single(created);
        Assert.Equal(2_000_000m, transfer.TransferOriginalAmount);
        Assert.Equal("RUB", transfer.OriginalCurrencyCode);
        Assert.Equal(90m, transfer.TransferPerUsdRate);
        Assert.Equal(SupplierBalanceTransferStatus.Active, transfer.Status);
        Assert.Equal(UsdContractId, transfer.ContractId);
        Assert.Equal(1, transfer.CompanyId);
    }

    // ۸) تقسیم بین چند قرارداد در یک ثبت — همه در یک Batch.
    [Fact]
    public async Task Transfer_Split_Across_Contracts_Shares_One_Batch()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m,
            (UsdContractId, 3_000_000m, 1m),
            (UsdContract2Id, 2_000_000m, 1m)));

        Assert.Equal(2, created.Count);
        Assert.Single(created.Select(t => t.BatchId).Distinct());
        Assert.Equal(30_000m, created[0].TransferValueUsd);
        Assert.Equal(20_000m, created[1].TransferValueUsd);
    }

    // ۹) نرخ روز متفاوت از نرخ تاریخی → ارزش دالری از نرخ روز می‌آید.
    // نرخ‌ها عمداً در ۶ رقم اعشار دقیق‌اند (۱/۱۰۰ و ۱/۸۰) تا تست، گِردکردن numeric(18,6)
    // سیستم را با خطای منطقی اشتباه نگیرد.
    [Fact]
    public async Task Transfer_Uses_Transfer_Day_Rate_Not_Historical_Rate()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);   // ارزش تاریخی 100,000 USD
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 80m, (UsdContractId, 1_800_000m, 1m))));

        Assert.Equal(18_000m, transfer.HistoricalAmountUsd);  // 1.8M ÷ 100
        Assert.Equal(22_500m, transfer.TransferValueUsd);     // 1.8M ÷ 80
        Assert.Equal(22_500m, transfer.TransferContractCurrencyAmount);
    }

    // ۱۰) سود نرخ ارز: ارز مانده قوی‌تر شده (نرخ روز کمتر از نرخ تاریخی).
    [Fact]
    public async Task Transfer_With_Stronger_Currency_Books_Exchange_Gain()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 80m, (UsdContractId, 1_800_000m, 1m))));

        Assert.Equal(SarrafSettlementDifferenceType.Gain, transfer.ExchangeDifferenceType);
        Assert.Equal(4500m, transfer.ExchangeDifferenceUsd);  // 22,500 − 18,000
        Assert.NotNull(transfer.ExchangeDifferenceLedgerEntryId);

        var gainRow = await db.LedgerEntries.SingleAsync(
            l => l.SourceType == SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType);
        Assert.Equal(LedgerSide.Credit, gainRow.Side);
        Assert.Equal(4500m, gainRow.AmountUsd);
    }

    // ۱۱) زیان نرخ ارز: ارز مانده ضعیف‌تر شده.
    [Fact]
    public async Task Transfer_With_Weaker_Currency_Books_Exchange_Loss()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 80m);   // ارزش تاریخی 125,000 USD
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m, (UsdContractId, 1_800_000m, 1m))));

        Assert.Equal(22_500m, transfer.HistoricalAmountUsd);  // 1.8M ÷ 80
        Assert.Equal(18_000m, transfer.TransferValueUsd);     // 1.8M ÷ 100
        Assert.Equal(SarrafSettlementDifferenceType.Loss, transfer.ExchangeDifferenceType);
        Assert.Equal(-4500m, transfer.ExchangeDifferenceUsd);

        var lossRow = await db.LedgerEntries.SingleAsync(
            l => l.SourceType == SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType);
        Assert.Equal(LedgerSide.Debit, lossRow.Side);
        Assert.Equal(4500m, lossRow.AmountUsd);
    }

    // ۱۲) نرخ برابر → تفاوت صفر و هیچ سطر تسعیری ساخته نمی‌شود.
    [Fact]
    public async Task Transfer_With_Same_Rate_Has_No_Exchange_Row()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 90m, (UsdContractId, 1_800_000m, 1m))));

        Assert.Equal(SarrafSettlementDifferenceType.None, transfer.ExchangeDifferenceType);
        Assert.Equal(0m, transfer.ExchangeDifferenceUsd);
        Assert.Null(transfer.ExchangeDifferenceLedgerEntryId);
        Assert.False(await db.LedgerEntries.AnyAsync(
            l => l.SourceType == SupplierBalanceTransferService.ExchangeDifferenceLedgerSourceType));
    }

    // ---------- ۱۳: برگشت ----------

    // ۱۳) برگشت کامل انتقال — مانده برمی‌گردد و سطرهای معکوس کامل ثبت می‌شوند.
    [Fact]
    public async Task Reverse_Restores_Balance_And_Posts_Mirror_Rows()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var transfer = Assert.Single(await service.CreateAsync(Request(
            "RUB", perUsd: 80m, (UsdContractId, 1_800_000m, 1m))));

        var afterTransfer = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        Assert.Equal(82_000m, Company1(afterTransfer).TransferableTotalUsd);

        await service.ReverseAsync(new SupplierBalanceTransferReverseRequest(transfer.Id, "اشتباه ثبت شده بود", "tester"));

        var afterReversal = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        Assert.Equal(100_000m, Company1(afterReversal).TransferableTotalUsd);
        Assert.Equal(10_000_000m, Assert.Single(Company1(afterReversal).Buckets).RemainingOriginalAmount);

        // سطر معکوس تسعیر هم ثبت شده تا ثبت‌های برگشت متوازن بمانند.
        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            l => l.SourceType == SupplierBalanceTransferService.ReversalLedgerSourceType));
        Assert.Equal(1, await db.LedgerEntries.CountAsync(
            l => l.SourceType == SupplierBalanceTransferService.ExchangeDifferenceReversalLedgerSourceType));
    }

    // ---------- ۱۴ تا ۱۷: کنترول‌های اجباری ----------

    // ۱۴) جلوگیری از انتقال بیشتر از مانده.
    [Fact]
    public async Task Transfer_Exceeding_Balance_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 1_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m, (UsdContractId, 1_500_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_EXCEEDS_BALANCE", ex.Code);
        Assert.Empty(db.SupplierBalanceTransfers);
    }

    // ۱۵) جلوگیری از قرارداد تأمین‌کننده دیگر.
    [Fact]
    public async Task Transfer_To_Other_Supplier_Contract_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 1_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m, (OtherSupplierContractId, 100_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_SUPPLIER_MISMATCH", ex.Code);
    }

    // ۱۶) جلوگیری از قرارداد شرکت دیگر در یک ثبت.
    [Fact]
    public async Task Transfer_Across_Two_Companies_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m,
            (UsdContractId, 1_000_000m, 1m),
            (OtherCompanyContractId, 1_000_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_COMPANY_MISMATCH", ex.Code);
    }

    // ۱۷) جلوگیری از ثبت هم‌زمان تکراری روی یک قرارداد در یک فورم.
    [Fact]
    public async Task Transfer_With_Duplicate_Contract_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m,
            (UsdContractId, 1_000_000m, 1m),
            (UsdContractId, 1_000_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_DUPLICATE_CONTRACT", ex.Code);
    }

    // ---------- ۱۸ تا ۲۰: بی‌اثری روی بقیهٔ سیستم ----------

    // ۱۸) مانده کلی تأمین‌کننده بعد از انتقال تغییر نمی‌کند.
    [Fact]
    public async Task Transfer_Keeps_Supplier_Net_Balance_Unchanged()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        var balanceService = new SupplierTransferableBalanceService(db);
        var before = Company1(await balanceService.GetAsync(SupplierId)).NetAccountBalanceUsd;

        await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m, (UsdContractId, 1_800_000m, 1m)));

        var after = Company1(await balanceService.GetAsync(SupplierId)).NetAccountBalanceUsd;
        Assert.Equal(before, after);
    }

    // ۱۹) بانک و صندوق دست‌نخورده می‌ماند: هیچ PaymentTransaction ساخته نمی‌شود.
    [Fact]
    public async Task Transfer_Creates_No_Payment_And_No_Cash_Movement()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();
        var paymentsBefore = await db.PaymentTransactions.CountAsync();

        await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 100m, (UsdContractId, 1_800_000m, 1m)));

        Assert.Equal(paymentsBefore, await db.PaymentTransactions.CountAsync());
        Assert.False(await db.LedgerEntries.AnyAsync(l =>
            l.SourceType == SupplierBalanceTransferService.LedgerSourceType && l.CustomerId != null));
    }

    // ۲۰) دو سطر متوازن ساخته می‌شود و قرارداد مقصد دقیقاً یک‌بار ثبت می‌گیرد (بدون Duplicate).
    [Fact]
    public async Task Transfer_Posts_Exactly_One_Contract_Row_And_One_Free_Row()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        await NewTransferService(db).CreateAsync(Request(
            "RUB", perUsd: 90m, (UsdContractId, 1_800_000m, 1m)));

        var rows = await db.LedgerEntries
            .Where(l => l.SourceType == SupplierBalanceTransferService.LedgerSourceType)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var freeRow = Assert.Single(rows.Where(r => r.ContractId == null));
        var contractRow = Assert.Single(rows.Where(r => r.ContractId == UsdContractId));
        Assert.Equal(LedgerSide.Credit, freeRow.Side);
        Assert.Equal(LedgerSide.Debit, contractRow.Side);
        Assert.Equal(freeRow.AmountUsd, contractRow.AmountUsd);
        Assert.All(rows, r => Assert.Equal(SupplierId, r.SupplierId));
    }

    // ---------- محافظت اضافی: دوباره‌شماری با موتور قدیمی ----------

    // تخصیص فعال قدیمی (SupplierPaymentAllocation) باید از مانده قابل انتقال کم شود،
    // وگرنه یک پول دوبار منتقل می‌شود.
    [Fact]
    public async Task Legacy_Allocation_Reduces_Transferable_Balance()
    {
        await using var db = await NewDbAsync();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 90,
            PaymentDate = new DateTime(2026, 2, 1),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = SupplierId,
            Amount = 100_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100_000m
        });
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 100_000m, "USD", 100_000m, 1m, sourceId: 90);
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            Id = 500,
            PaymentTransactionId = 90,
            ContractId = UsdContractId,
            AllocationDate = new DateTime(2026, 2, 5),
            AllocatedPaymentAmount = 30_000m,
            PaymentCurrencyCode = "USD",
            AllocatedBookAmountUsd = 30_000m,
            AllocatedValueUsdAtAllocation = 30_000m,
            Status = SupplierPaymentAllocationStatus.Active
        });
        // همان دو سطر متوازنی که موتور قدیمی می‌سازد.
        AddSupplierLedger(db, "SupplierPaymentAllocation", LedgerSide.Credit, 30_000m, "USD", 30_000m, 1m, sourceId: 500);
        AddSupplierLedger(db, "SupplierPaymentAllocation", LedgerSide.Debit, 30_000m, "USD", 30_000m, 1m, sourceId: 500, contractId: UsdContractId);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(100_000m, Company1(balance).NetAccountBalanceUsd);
        Assert.Equal(30_000m, Company1(balance).ConsumedByLegacyAllocationsUsd);
        Assert.Equal(70_000m, Company1(balance).TransferableTotalUsd);
    }

    // FIFO: قدیمی‌ترین پول اول مصرف‌شده فرض می‌شود، پس مانده از تازه‌ترین ارز می‌آید.
    [Fact]
    public async Task Fifo_Keeps_Newest_Money_As_Transferable()
    {
        await using var db = await NewDbAsync();
        // قدیمی: 9,000,000 RUB (100,000 USD) — تازه: 50,000 USD — بارگیری 100,000 USD
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 100_000m, "RUB", 9_000_000m, 0.0111111m,
            date: new DateTime(2026, 1, 1));
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 50_000m, "USD", 50_000m, 1m,
            date: new DateTime(2026, 3, 1));
        AddSupplierLedger(db, "Loading", LedgerSide.Credit, 100_000m, "USD", 100_000m, 1m,
            date: new DateTime(2026, 2, 1), contractId: UsdContractId);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(50_000m, Company1(balance).TransferableTotalUsd);
        var bucket = Assert.Single(Company1(balance).Buckets);
        Assert.Equal("USD", bucket.CurrencyCode);
        Assert.Equal(50_000m, bucket.RemainingOriginalAmount);
    }

    // تأمین‌کنندهٔ بدهکار (طلب منفی) هیچ مانده قابل انتقالی ندارد.
    [Fact]
    public async Task Negative_Claim_Has_No_Transferable_Balance()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 20_000m, "USD", 20_000m, 1m);
        AddSupplierLedger(db, "Loading", LedgerSide.Credit, 60_000m, "USD", 60_000m, 1m, contractId: UsdContractId);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(-40_000m, Company1(balance).NetAccountBalanceUsd);
        Assert.Equal(0m, Company1(balance).ClaimUsd);
        Assert.False(Company1(balance).HasTransferable);
    }

    // پول برگشتی از تأمین‌کننده طلب را کم می‌کند.
    [Fact]
    public async Task Supplier_Refund_Reduces_Transferable_Balance()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 50_000m, "USD", 50_000m, 1m);
        AddSupplierLedger(db, "SupplierReceipt", LedgerSide.Credit, 20_000m, "USD", 20_000m, 1m);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(30_000m, Company1(balance).ClaimUsd);
        Assert.Equal(30_000m, Company1(balance).TransferableTotalUsd);
    }

    // برگشت دوباره اجازه ندارد.
    [Fact]
    public async Task Second_Reversal_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var transfer = Assert.Single(await service.CreateAsync(Request(
            "RUB", perUsd: 90m, (UsdContractId, 900_000m, 1m))));
        await service.ReverseAsync(new SupplierBalanceTransferReverseRequest(transfer.Id, "دلیل", null));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ReverseAsync(
            new SupplierBalanceTransferReverseRequest(transfer.Id, "دوباره", null)));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_ALREADY_REVERSED", ex.Code);
    }

    // برگشت بدون دلیل اجازه ندارد.
    [Fact]
    public async Task Reversal_Without_Reason_Is_Rejected()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 9_000_000m, perUsd: 90m);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var transfer = Assert.Single(await service.CreateAsync(Request(
            "RUB", perUsd: 90m, (UsdContractId, 900_000m, 1m))));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ReverseAsync(
            new SupplierBalanceTransferReverseRequest(transfer.Id, "   ", null)));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_REVERSAL_REASON_REQUIRED", ex.Code);
    }

    // انتقال به قرارداد روبلی: معادل ارز قرارداد از ارزش روز انتقال ساخته می‌شود.
    [Fact]
    public async Task Transfer_To_Rub_Contract_Converts_Through_Usd()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 50_000m, "USD", 50_000m, 1m);
        await db.SaveChangesAsync();

        var transfer = Assert.Single(await NewTransferService(db).CreateAsync(Request(
            "USD", perUsd: 1m, (RubContractId, 10_000m, 80m))));

        Assert.Equal(10_000m, transfer.TransferValueUsd);
        Assert.Equal("RUB", transfer.ContractCurrencyCode);
        Assert.Equal(80m, transfer.ContractCurrencyPerUsdRate);
        Assert.Equal(800_000m, transfer.TransferContractCurrencyAmount);
    }

    // ---------- مالکیت شرکت: تأمین‌کننده مشترک بین دو شرکت ----------

    // مانده هر شرکت جدا نمایش داده می‌شود و با هم تجمیع نمی‌شود.
    [Fact]
    public async Task Shared_Supplier_Keeps_Each_Company_Balance_Separate()
    {
        await using var db = await NewDbAsync();
        // شرکت ۱: پرداخت روی قرارداد دالریِ شرکت ۱
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 60_000m, "USD", 60_000m, 1m,
            contractId: UsdContractId, sourceId: 1);
        // شرکت ۲: پرداخت روی قرارداد شرکت ۲
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 25_000m, "USD", 25_000m, 1m,
            contractId: OtherCompanyContractId, sourceId: 2);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Equal(2, balance.Companies.Count);
        Assert.Equal(60_000m, Company(balance, CompanyId1).TransferableTotalUsd);
        Assert.Equal(25_000m, Company(balance, CompanyId2).TransferableTotalUsd);
        // هرگز تجمیع نمی‌شوند: هیچ شرکتی کل ۸۵٬۰۰۰ را نشان نمی‌دهد.
        Assert.DoesNotContain(balance.Companies, c => c.TransferableTotalUsd == 85_000m);
    }

    // طلب شرکت A نباید به قرارداد شرکت B منتقل شود.
    [Fact]
    public async Task Claim_Of_Company_A_Cannot_Go_To_Contract_Of_Company_B()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 60_000m, "USD", 60_000m, 1m,
            contractId: UsdContractId, sourceId: 1);
        await db.SaveChangesAsync();

        // مانده مال شرکت ۱ است ولی قرارداد مقصد مال شرکت ۲.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(
            RequestFor(CompanyId1, "USD", 1m, (OtherCompanyContractId, 10_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_COMPANY_MISMATCH", ex.Code);
        Assert.Empty(db.SupplierBalanceTransfers);
    }

    // ادعای شرکت ۲ نمی‌تواند بیشتر از مانده خودِ شرکت ۲ مصرف شود، حتی اگر شرکت ۱ پول زیاد داشته باشد.
    [Fact]
    public async Task Company_Cannot_Spend_Another_Companys_Balance()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 60_000m, "USD", 60_000m, 1m,
            contractId: UsdContractId, sourceId: 1);
        AddSupplierLedger(db, "SupplierPayment", LedgerSide.Debit, 5_000m, "USD", 5_000m, 1m,
            contractId: OtherCompanyContractId, sourceId: 2);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => NewTransferService(db).CreateAsync(
            RequestFor(CompanyId2, "USD", 1m, (OtherCompanyContractId, 20_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_EXCEEDS_BALANCE", ex.Code);
    }

    // منبعی که شرکتش از سند اثبات نمی‌شود در سطل «سطح گروه» قابل انتقال است.
    // این عمداً برخلاف رفتار قبلی است: پنهان‌کردن مانده باعث می‌شد کاربر پولش را در UI نبیند.
    [Fact]
    public async Task Source_Without_Provable_Company_Is_Transferable_At_Group_Level()
    {
        await using var db = await NewDbAsync();
        // مانده اول دوره بدون قرارداد و بدون پرداخت مرتبط → شرکت قابل اثبات نیست.
        AddSupplierLedgerWithUnknownCompany(db, "OpeningBalance", LedgerSide.Debit, 40_000m, "USD", 40_000m, 1m, 777);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Empty(balance.Companies);
        Assert.True(balance.HasTransferable);
        Assert.NotNull(balance.GroupLevel);
        Assert.Equal(SupplierTransferableBalance.GroupLevelCompanyId, balance.GroupLevel!.CompanyId);
        Assert.Equal(40_000m, balance.GroupLevel.TransferableTotalUsd);
        Assert.Equal(40_000m, balance.TransferableTotalUsd);

        // شمارش شفافیت دست‌نخورده می‌ماند.
        Assert.True(balance.UnknownCompany.HasAny);
        Assert.Equal(40_000m, balance.UnknownCompany.OutflowUsd);
        Assert.Equal(1, balance.UnknownCompany.RowCount);
    }

    // پرداخت بدون قرارداد ولی با CompanyId ثبت‌شده، شرکتش اثبات می‌شود و قابل انتقال است.
    [Fact]
    public async Task Contractless_Payment_With_CompanyId_Is_Attributed_To_That_Company()
    {
        await using var db = await NewDbAsync();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 91,
            PaymentDate = new DateTime(2026, 2, 1),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = SupplierId,
            CompanyId = CompanyId2,
            Amount = 15_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 15_000m
        });
        AddSupplierLedgerWithUnknownCompany(db, "SupplierPayment", LedgerSide.Debit, 15_000m, "USD", 15_000m, 1m, 91);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.False(balance.UnknownCompany.HasAny);
        Assert.Equal(15_000m, Company(balance, CompanyId2).TransferableTotalUsd);
        Assert.Equal(0m, Company(balance, CompanyId1).TransferableTotalUsd);
    }

    // Guard دفتر کل مرکزی: اگر حسابداری و Pilot روشن شود ولی Adapter نباشد، ثبت متوقف می‌شود.
    [Fact]
    public async Task Transfer_Is_Blocked_When_Central_Accounting_Pilot_Is_On_Without_Adapter()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new PTGOilSystem.Web.Configuration.AccountingOptions
        {
            Enabled = true,
            Pilots = new PTGOilSystem.Web.Configuration.AccountingPilotOptions { SupplierBalanceTransfer = true }
        });
        var service = new SupplierBalanceTransferService(db, new SupplierTransferableBalanceService(db), options);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(Request(
            "RUB", perUsd: 100m, (UsdContractId, 1_000_000m, 1m))));

        Assert.Equal("SUPPLIER_BALANCE_TRANSFER_ACCOUNTING_ADAPTER_MISSING", ex.Code);
        Assert.Empty(db.SupplierBalanceTransfers);
    }

    // با حسابداری خاموش (وضعیت فعلی) رفتار دقیقاً مثل قبل است.
    [Fact]
    public async Task Transfer_Works_Normally_When_Central_Accounting_Is_Disabled()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 10_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new PTGOilSystem.Web.Configuration.AccountingOptions
        {
            Enabled = false,
            Pilots = new PTGOilSystem.Web.Configuration.AccountingPilotOptions { SupplierBalanceTransfer = true }
        });
        var service = new SupplierBalanceTransferService(db, new SupplierTransferableBalanceService(db), options);

        var created = await service.CreateAsync(Request("RUB", perUsd: 100m, (UsdContractId, 1_000_000m, 1m)));

        Assert.Single(created);
        Assert.Equal(CompanyId1, created[0].CompanyId);
    }


    // خطای سرور باید در ModelState بنشیند تا در فورم دیده شود.
    [Fact]
    public async Task Controller_Post_Over_Balance_Puts_Error_In_ModelState()
    {
        await using var db = await NewDbAsync();
        AddRubClaim(db, 1_000_000m, perUsd: 100m);
        await db.SaveChangesAsync();

        var controller = new PTGOilSystem.Web.Controllers.SupplierBalanceTransfersController(
            db,
            new SupplierTransferableBalanceService(db),
            NewTransferService(db),
            new PTGOilSystem.Web.Services.AuditService(db),
            new PricingService(db))
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(), new TransferTestTempDataProvider())
        };

        var model = new PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferCreateViewModel
        {
            SupplierId = SupplierId,
            CompanyId = CompanyId1,
            CurrencyCode = "RUB",
            TransferDate = new DateTime(2026, 4, 1),
            TransferPerUsdRate = 100m,
            Lines =
            [
                new PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferLineViewModel
                {
                    ContractId = UsdContractId,
                    TransferOriginalAmount = 5_000_000m,
                    ContractCurrencyPerUsdRate = 1m
                }
            ]
        };

        var result = await controller.Create(model);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        var message = controller.ModelState[string.Empty]!.Errors[0].ErrorMessage;
        Assert.Contains("مانده کافی نیست", message);
    }

    // ---------- سطح گروه: منبعِ بدون شرکتِ اثبات‌شده ----------

    // «پرداخت از طریق صراف» بدون قرارداد: نه PaymentTransaction دارد نه CashAccount، پس شرکتش
    // اثبات نمی‌شود — ولی مانده باید در سطل سطح گروه دیده و قابل انتقال باشد.
    [Fact]
    public async Task ViaSarraf_Payment_Without_Contract_Lands_In_Group_Level_Pool()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 2_551_017.15m, "RUB", 200_000_000m, 0.01275509m, 55);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Empty(balance.Companies);
        Assert.NotNull(balance.GroupLevel);
        Assert.Equal(2_551_017.15m, balance.GroupLevel!.TransferableTotalUsd);
        var bucket = Assert.Single(balance.GroupLevel.Buckets);
        Assert.Equal("RUB", bucket.CurrencyCode);
        Assert.Equal(200_000_000m, bucket.RemainingOriginalAmount);
    }

    // اگر لنگِ مکملِ همان گروه صرافی قرارداد داشته باشد، شرکت از همان اثبات می‌شود و سند
    // بی‌دلیل به سطح گروه نمی‌افتد.
    [Fact]
    public async Task ViaSarraf_Payment_Resolves_Company_From_Group_Partner_Leg()
    {
        await using var db = await NewDbAsync();
        var groupId = Guid.NewGuid();
        // لنگ تأمین‌کننده بدون قرارداد.
        AddLedgerRow(db, "SupplierViaSarrafPayment", LedgerSide.Debit, 5_000m, "RUB", 400_000m, 0.0125m,
            contractId: null, sourceId: 55, date: null, viaSarrafGroupId: groupId);
        // لنگ بدهی صراف با قرارداد شرکت ۲.
        AddLedgerRow(db, "SupplierViaSarrafPayable", LedgerSide.Credit, 5_000m, "RUB", 400_000m, 0.0125m,
            contractId: OtherCompanyContractId, sourceId: 55, date: null, viaSarrafGroupId: groupId,
            supplierId: null);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Null(balance.GroupLevel);
        Assert.Equal(5_000m, Company(balance, CompanyId2).TransferableTotalUsd);
    }

    // تسویهٔ صراف بدون قرارداد ولی با CashAccount شرکت‌دار: شرکت اثبات می‌شود، سطح گروه نمی‌ماند.
    [Fact]
    public async Task SarrafSettlement_Without_Contract_Resolves_Company_From_CashAccount()
    {
        await using var db = await NewDbAsync();
        db.CashAccounts.Add(new CashAccount
        {
            Id = 7,
            Code = "CASH-CO2",
            Name = "Cash Company 2",
            Currency = "USD",
            CompanyId = CompanyId2,
            IsActive = true
        });
        var ledgerId = AddSupplierLedgerWithUnknownCompany(
            db, "SarrafSettlement", LedgerSide.Debit, 9_000m, "USD", 9_000m, 1m, 33);
        // سطر تسویهٔ صراف فقط وقتی «اثر جاری» است که خودِ تسویهٔ Posted به آن اشاره کند.
        db.SarrafSettlements.Add(new SarrafSettlement
        {
            Id = 33,
            SettlementDate = new DateTime(2026, 2, 1),
            SarrafId = 1,
            SupplierId = SupplierId,
            CashAccountId = 7,
            LedgerEntryId = ledgerId,
            Status = SarrafSettlementStatus.Posted,
            RequestedCurrency = "USD",
            SarrafCurrency = "USD",
            SupplierAcceptedCurrency = "USD"
        });
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.Null(balance.GroupLevel);
        Assert.Equal(9_000m, Company(balance, CompanyId2).TransferableTotalUsd);
    }

    // انتقال سطح گروه به یک قرارداد: شرکت ردیف از قرارداد مقصد گرفته می‌شود و صفر ذخیره نمی‌شود.
    [Fact]
    public async Task GroupLevel_Transfer_To_Single_Contract_Takes_Company_From_Contract()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(
            RequestFor(SupplierTransferableBalance.GroupLevelCompanyId, "RUB", 100m,
                (UsdContractId, 4_000_000m, 1m)));

        var transfer = Assert.Single(created);
        Assert.Equal(CompanyId1, transfer.CompanyId);
        Assert.Equal(40_000m, transfer.TransferValueUsd);
        Assert.Equal(40_000m, transfer.HistoricalAmountUsd);
        Assert.Equal(SarrafSettlementDifferenceType.None, transfer.ExchangeDifferenceType);
    }

    // انتقال یک‌بارهٔ سطح گروه به قراردادهای دو شرکت مختلف — بدون تخصیص دستی قبلی و بدون
    // عبور از ContractBalanceTransfers. هر ردیف شرکت خودش را از قرارداد مقصدش می‌گیرد.
    [Fact]
    public async Task GroupLevel_Transfer_Splits_Across_Contracts_Of_Different_Companies()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        var created = await NewTransferService(db).CreateAsync(
            RequestFor(SupplierTransferableBalance.GroupLevelCompanyId, "RUB", 100m,
                (UsdContractId, 6_000_000m, 1m),
                (OtherCompanyContractId, 4_000_000m, 1m)));

        Assert.Equal(2, created.Count);
        Assert.Equal(CompanyId1, created.Single(t => t.ContractId == UsdContractId).CompanyId);
        Assert.Equal(CompanyId2, created.Single(t => t.ContractId == OtherCompanyContractId).CompanyId);
        // یک BatchId مشترک: هر دو ردیف حاصل یک ثبت‌اند.
        Assert.Single(created.Select(t => t.BatchId).Distinct());

        // ثبت‌های دفتر برای هر ردیف جفتِ متوازن‌اند: Credit مخزن آزاد + Debit قرارداد.
        var transferRows = await db.LedgerEntries
            .Where(l => l.SourceType == SupplierBalanceTransferService.LedgerSourceType)
            .ToListAsync();
        Assert.Equal(4, transferRows.Count);
        Assert.Equal(
            transferRows.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
            transferRows.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd));

        // هر ردیف در شرکت مقصد خودش خالصِ صفر است: مانده کلی تأمین‌کننده تکان نمی‌خورد.
        foreach (var transfer in created)
        {
            var rows = transferRows.Where(l => l.SourceId == transfer.Id).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Null(rows.Single(l => l.Side == LedgerSide.Credit).ContractId);
            Assert.Equal(transfer.ContractId, rows.Single(l => l.Side == LedgerSide.Debit).ContractId);
        }
    }

    // مصرفِ انتقالِ سطح گروه باید از خودِ سطل سطح گروه کم شود، نه از شرکت مقصد.
    // بدون این، همان پول دوباره قابل انتقال می‌ماند (خرج دوباره).
    [Fact]
    public async Task GroupLevel_Transfer_Reduces_The_Group_Level_Pool()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        await NewTransferService(db).CreateAsync(
            RequestFor(SupplierTransferableBalance.GroupLevelCompanyId, "RUB", 100m,
                (UsdContractId, 4_000_000m, 1m)));

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.NotNull(balance.GroupLevel);
        Assert.Equal(60_000m, balance.GroupLevel!.TransferableTotalUsd);
        Assert.Equal(6_000_000m, Assert.Single(balance.GroupLevel.Buckets).RemainingOriginalAmount);
        // شرکت مقصد از این مصرف چیزی کم نمی‌کند؛ اثر انتقال روی خودش خالصِ صفر است.
        Assert.Equal(0m, Company1(balance).TransferableTotalUsd);
    }

    // انتقال سطح گروه بیشتر از مانده رد می‌شود.
    [Fact]
    public async Task GroupLevel_Transfer_Cannot_Exceed_Group_Level_Balance()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(
            RequestFor(SupplierTransferableBalance.GroupLevelCompanyId, "RUB", 100m,
                (UsdContractId, 12_000_000m, 1m))));
    }

    // برگشت کامل انتقال سطح گروه: مانده دقیقاً به سطل سطح گروه برمی‌گردد و دفتر متوازن می‌ماند.
    [Fact]
    public async Task Reversing_GroupLevel_Transfer_Returns_Balance_To_Group_Level_Pool()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        var service = NewTransferService(db);
        var created = await service.CreateAsync(
            RequestFor(SupplierTransferableBalance.GroupLevelCompanyId, "RUB", 100m,
                (UsdContractId, 4_000_000m, 1m),
                (OtherCompanyContractId, 3_000_000m, 1m)));

        foreach (var transfer in created)
        {
            await service.ReverseAsync(new SupplierBalanceTransferReverseRequest(transfer.Id, "اشتباه ثبت", "tester"));
        }

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);

        Assert.NotNull(balance.GroupLevel);
        Assert.Equal(100_000m, balance.GroupLevel!.TransferableTotalUsd);
        Assert.Equal(10_000_000m, Assert.Single(balance.GroupLevel.Buckets).RemainingOriginalAmount);

        var allRows = await db.LedgerEntries
            .Where(l => l.SourceType == SupplierBalanceTransferService.LedgerSourceType
                || l.SourceType == SupplierBalanceTransferService.ReversalLedgerSourceType)
            .ToListAsync();
        Assert.Equal(
            allRows.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
            allRows.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd));
    }

    // کارت جزئیات تأمین‌کننده: مانده سطح گروه پنهان نمی‌شود و دکمهٔ انتقال دارد.
    [Fact]
    public async Task Supplier_Card_Shows_Group_Level_Pool()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 2_551_017.15m, "RUB", 200_000_000m, 0.01275509m, 55);
        await db.SaveChangesAsync();

        var balance = await new SupplierTransferableBalanceService(db).GetAsync(SupplierId);
        var card = new PTGOilSystem.Web.Models.Suppliers.SupplierTransferableBalanceCardViewModel
        {
            SupplierId = SupplierId,
            UnknownCompanyOutflowUsd = balance.UnknownCompany.OutflowUsd,
            UnknownCompanyRowCount = balance.UnknownCompany.RowCount,
            Companies = balance.AllPools
                .Select(c => new PTGOilSystem.Web.Models.Suppliers.SupplierCompanyBalanceViewModel
                {
                    CompanyId = c.CompanyId,
                    CompanyName = c.CompanyName,
                    ClaimUsd = c.ClaimUsd,
                    TransferableTotalUsd = c.TransferableTotalUsd,
                    IsGroupLevel = c.CompanyId == SupplierTransferableBalance.GroupLevelCompanyId,
                    Buckets = PTGOilSystem.Web.Controllers.SupplierBalanceTransfersController.MapBuckets(c)
                })
                .ToList()
        };

        Assert.True(card.HasTransferable);
        var pool = Assert.Single(card.Companies);
        Assert.True(pool.IsGroupLevel);
        Assert.Equal(2_551_017.15m, pool.TransferableTotalUsd);
    }

    // فورم سطح گروه: قراردادهای همهٔ شرکت‌های همان تأمین‌کننده قابل انتخاب‌اند تا کاربر
    // مجبور به تخصیص دستی و تقسیم دوباره نشود.
    [Fact]
    public async Task GroupLevel_Create_Form_Lists_Contracts_Of_All_Companies()
    {
        await using var db = await NewDbAsync();
        AddSupplierLedgerWithUnknownCompany(
            db, "SupplierViaSarrafPayment", LedgerSide.Debit, 100_000m, "RUB", 10_000_000m, 0.01m, 55);
        await db.SaveChangesAsync();

        var controller = new PTGOilSystem.Web.Controllers.SupplierBalanceTransfersController(
            db,
            new SupplierTransferableBalanceService(db),
            NewTransferService(db),
            new PTGOilSystem.Web.Services.AuditService(db),
            new PricingService(db))
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(), new TransferTestTempDataProvider())
        };

        var result = await controller.Create(SupplierId, SupplierTransferableBalance.GroupLevelCompanyId);

        var view = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
        var model = Assert.IsType<PTGOilSystem.Web.Models.Suppliers.SupplierBalanceTransferCreateViewModel>(view.Model);
        Assert.True(model.IsGroupLevel);
        Assert.Equal("RUB", model.CurrencyCode);

        var options = (Microsoft.AspNetCore.Mvc.Rendering.SelectList)controller.ViewBag.TransferContracts;
        var ids = options.Select(o => int.Parse(o.Value)).ToList();
        Assert.Contains(UsdContractId, ids);          // شرکت ۱
        Assert.Contains(OtherCompanyContractId, ids); // شرکت ۲
        Assert.DoesNotContain(OtherSupplierContractId, ids);
    }

    // ---------- کمکی‌ها ----------

    private sealed class TransferTestTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context)
            => new Dictionary<string, object>();

        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static SupplierBalanceTransferService NewTransferService(ApplicationDbContext db)
        => new(db, new SupplierTransferableBalanceService(db));

    /// <summary>مانده شرکت ۱ — پیش‌فرض بیشتر تست‌ها. اگر شرکت مانده نداشته باشد یک نمونهٔ صفر می‌دهد.</summary>
    private static SupplierCompanyTransferableBalance Company1(SupplierTransferableBalance balance)
        => Company(balance, CompanyId1);

    private static SupplierCompanyTransferableBalance Company(SupplierTransferableBalance balance, int companyId)
        => balance.Company(companyId)
            ?? new SupplierCompanyTransferableBalance(companyId, "-", 0m, 0m, 0m, 0m, 0m, []);

    private static SupplierBalanceTransferCreateRequest Request(
        string currency,
        decimal perUsd,
        params (int ContractId, decimal Amount, decimal ContractPerUsd)[] lines)
        => RequestFor(CompanyId1, currency, perUsd, lines);

    private static SupplierBalanceTransferCreateRequest RequestFor(
        int companyId,
        string currency,
        decimal perUsd,
        params (int ContractId, decimal Amount, decimal ContractPerUsd)[] lines)
        => new(
            SupplierId,
            companyId,
            new DateTime(2026, 4, 1),
            currency,
            perUsd,
            lines.Select(l => new SupplierBalanceTransferLineRequest(l.ContractId, l.Amount, l.ContractPerUsd)).ToList(),
            "REF-1",
            null,
            "tester");

    /// <summary>
    /// طلب روبلی با نرخ ثبت‌شدهٔ مستقیم — همان چیزی که موتور جدید می‌سازد.
    ///
    /// قبلاً این helper نرخ را با ۶ رقم اعشار معکوس می‌کرد و ارزش دفتری ۹ میلیون روبل با
    /// نرخ ۹۰ به‌جای ۱۰۰,۰۰۰ دالر، ۹۹,۹۹۹ درمی‌آمد. یعنی خودِ فیکسچر همان انحرافی را
    /// داشت که این ماژول برای حذفش اصلاح شد، و چون انتقال هم با همان نرخ خراب حساب
    /// می‌شد، اختلاف پنهان می‌ماند. حالا نرخ مستقیم ذخیره می‌شود و مبالغ دقیق‌اند.
    /// </summary>
    private static void AddRubClaim(ApplicationDbContext db, decimal rubAmount, decimal perUsd)
    {
        var fxRateToUsd = FxRateMath.ToUsdFromPerUsd(perUsd);
        var amountUsd = decimal.Round(rubAmount / perUsd, 4, MidpointRounding.AwayFromZero);
        AddLedgerRow(db, "SupplierPayment", LedgerSide.Debit, amountUsd, "RUB", rubAmount, fxRateToUsd,
            UsdContractId, null, null, perUsd);
    }

    private static int _nextLedgerId = 1000;

    /// <summary>
    /// سطر دفتر تأمین‌کننده. اگر قرارداد داده نشود، به قرارداد دالریِ شرکت ۱ وصل می‌شود تا
    /// شرکتِ منبع اثبات‌شدنی باشد — مثل داده واقعی. برای آزمودن «شرکت نامعلوم» از
    /// <see cref="AddSupplierLedgerWithUnknownCompany"/> استفاده می‌شود.
    /// </summary>
    private static void AddSupplierLedger(
        ApplicationDbContext db,
        string sourceType,
        LedgerSide side,
        decimal amountUsd,
        string sourceCurrency,
        decimal sourceAmount,
        decimal fxRateToUsd,
        int? contractId = null,
        int? sourceId = null,
        DateTime? date = null)
        => AddLedgerRow(db, sourceType, side, amountUsd, sourceCurrency, sourceAmount, fxRateToUsd,
            contractId ?? UsdContractId, sourceId, date);

    /// <summary>سطر بدون قرارداد و بدون سند قابل ردیابی — شرکتش اثبات نمی‌شود. شناسهٔ سطر را برمی‌گرداند.</summary>
    private static int AddSupplierLedgerWithUnknownCompany(
        ApplicationDbContext db,
        string sourceType,
        LedgerSide side,
        decimal amountUsd,
        string sourceCurrency,
        decimal sourceAmount,
        decimal fxRateToUsd,
        int sourceId)
        => AddLedgerRow(db, sourceType, side, amountUsd, sourceCurrency, sourceAmount, fxRateToUsd,
            null, sourceId, null);

    private static int AddLedgerRow(
        ApplicationDbContext db,
        string sourceType,
        LedgerSide side,
        decimal amountUsd,
        string sourceCurrency,
        decimal sourceAmount,
        decimal fxRateToUsd,
        int? contractId,
        int? sourceId,
        DateTime? date,
        // نرخ مستقیم «۱ دالر = چند واحد». null یعنی سطر Legacy بدون نرخ ثبت‌شده.
        decimal? perUsdRate = null,
        // شناسهٔ گروه «پرداخت از طریق صراف» — دو لنگِ مکمل زیر یک شناسه می‌نشینند.
        Guid? viaSarrafGroupId = null,
        // لنگ بدهی صراف تأمین‌کننده ندارد؛ برای همین قابل خالی‌کردن است.
        int? supplierId = SupplierId)
    {
        var ledgerId = _nextLedgerId++;
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = ledgerId,
            EntryDate = date ?? new DateTime(2026, 2, 1),
            Side = side,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceAmount = sourceAmount,
            SourceCurrencyCode = sourceCurrency,
            AppliedFxRateToUsd = fxRateToUsd,
            AppliedCurrencyPerUsdRate = perUsdRate,
            AppliedFxRateDate = date ?? new DateTime(2026, 2, 1),
            Description = sourceType,
            SourceType = sourceType,
            SourceId = sourceId ?? 1,
            SupplierId = supplierId,
            ContractId = contractId,
            ViaSarrafGroupId = viaSarrafGroupId
        });

        return ledgerId;
    }

    private static async Task<ApplicationDbContext> NewDbAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Products.Add(new Product { Id = 1, Code = "G92", Name = "Gasoline 92", UnitOfMeasure = "MT", IsActive = true });
        db.Companies.AddRange(
            new Company { Id = 1, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true },
            new Company { Id = 2, Code = "PTG2", Name = "PTG Two", Country = "AF", IsActive = true });
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH-USD", Name = "Cash USD", Currency = "USD", IsActive = true });
        db.Suppliers.AddRange(
            new Supplier { Id = SupplierId, Code = "SUP1", Name = "Supplier One", IsActive = true },
            new Supplier { Id = OtherSupplierId, Code = "SUP2", Name = "Supplier Two", IsActive = true });

        db.Contracts.AddRange(
            NewContract(RubContractId, "P-RUB", SupplierId, 1, "RUB"),
            NewContract(2, "P-RUB-2", SupplierId, 1, "RUB"),
            NewContract(OtherSupplierContractId, "P-OTHER", OtherSupplierId, 1, "RUB"),
            NewContract(UsdContractId, "P-USD-1", SupplierId, 1, "USD"),
            NewContract(UsdContract2Id, "P-USD-2", SupplierId, 1, "USD"),
            NewContract(OtherCompanyContractId, "P-CO2", SupplierId, 2, "USD"));

        await db.SaveChangesAsync();
        return db;
    }

    private static Contract NewContract(int id, string number, int supplierId, int companyId, string currency)
        => new()
        {
            Id = id,
            ContractNumber = number,
            ContractName = number,
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = companyId,
            ProductId = 1,
            SupplierId = supplierId,
            ContractDate = new DateTime(2026, 1, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 1000m,
            Currency = currency
        };
}
