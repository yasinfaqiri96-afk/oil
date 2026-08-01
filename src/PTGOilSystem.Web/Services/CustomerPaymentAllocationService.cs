using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public sealed record CustomerPaymentAllocationCreateRequest(
    int PaymentTransactionId,
    int PreSaleOrderId,
    DateTime AllocationDate,
    decimal AllocatedPaymentAmount,
    string? ReferenceNumber = null,
    string? Notes = null,
    string? CreatedByUserName = null);

public sealed record CustomerPaymentAllocationReverseRequest(
    int AllocationId,
    string? ReversalReason = null,
    string? ReversedByUserName = null);

public interface ICustomerPaymentAllocationService
{
    /// <summary>مانده قابل تخصیص یک دریافت، به ارز خودِ دریافت.</summary>
    Task<decimal> GetAllocatablePaymentAmountAsync(int paymentTransactionId, CancellationToken ct = default);

    /// <summary>مانده مالیِ یک پیش‌فروش به USD: مبلغ تعهد منهای تخصیص‌های فعال.</summary>
    Task<decimal> GetUnallocatedPreSaleAmountUsdAsync(int preSaleOrderId, CancellationToken ct = default);

    /// <summary>مانده مصرف‌نشدهٔ یک تخصیص به USD: ارزش تخصیص منهای مجموع Applicationهای فعال.</summary>
    Task<decimal> GetUnappliedAllocationUsdAsync(int allocationId, CancellationToken ct = default);

    Task<CustomerPaymentAllocation> AllocateAsync(
        CustomerPaymentAllocationCreateRequest request, CancellationToken ct = default);

    Task<CustomerPaymentAllocation> ReverseAsync(
        CustomerPaymentAllocationReverseRequest request, CancellationToken ct = default);
}

/// <summary>
/// تخصیص دریافت مشتری به پیش‌فروش.
///
/// این سرویس هیچ سند مالی نمی‌سازد: دریافت مشتری سند خودش را هنگام دریافت گرفته است
/// (نقد/بانک بدهکار، پیش‌دریافت مشتری بستانکار) و کالا هنوز تحویل نشده، پس نه درآمدی شناسایی
/// می‌شود و نه اختلاف تسعیری تحقق می‌یابد. تخصیص فقط می‌گوید چه مقدار از آن پول به کدام تعهد
/// تعلق دارد. مصرف واقعیِ پیش‌دریافت در ژورنالِ هر تحویل انجام می‌شود.
///
/// نرخ ارز از خودِ دریافت خوانده و قفل می‌شود؛ هیچ نرخ مصنوعی از نسبت مبالغ ساخته نمی‌شود و
/// دریافتی که نرخ معتبر ندارد اصلاً قابل تخصیص نیست.
/// </summary>
public sealed class CustomerPaymentAllocationService(ApplicationDbContext db)
    : ICustomerPaymentAllocationService
{
    private const decimal Epsilon = 0.0001m;

    public async Task<decimal> GetAllocatablePaymentAmountAsync(
        int paymentTransactionId, CancellationToken ct = default)
    {
        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentTransactionId, ct);
        if (payment is null)
        {
            return 0m;
        }

        var allocated = await db.CustomerPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PaymentTransactionId == paymentTransactionId
                && a.Status == CustomerPaymentAllocationStatus.Active)
            .SumAsync(a => (decimal?)a.AllocatedPaymentAmount, ct) ?? 0m;

        return decimal.Round(payment.Amount - allocated, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<decimal> GetUnallocatedPreSaleAmountUsdAsync(
        int preSaleOrderId, CancellationToken ct = default)
    {
        var order = await db.PreSaleOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == preSaleOrderId, ct);
        if (order is null)
        {
            return 0m;
        }

        var allocatedUsd = await GetAllocatedUsdAsync(preSaleOrderId, ct);
        return decimal.Round(order.TotalUsd - allocatedUsd, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<decimal> GetUnappliedAllocationUsdAsync(int allocationId, CancellationToken ct = default)
    {
        var allocation = await db.CustomerPaymentAllocations
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == allocationId, ct);
        if (allocation is null || allocation.Status != CustomerPaymentAllocationStatus.Active)
        {
            return 0m;
        }

        var applied = await db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => a.CustomerPaymentAllocationId == allocationId
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .SumAsync(a => (decimal?)a.AppliedAmountUsd, ct) ?? 0m;

        return decimal.Round(allocation.AllocatedAmountUsd - applied, 4, MidpointRounding.AwayFromZero);
    }

    public async Task<CustomerPaymentAllocation> AllocateAsync(
        CustomerPaymentAllocationCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AllocatedPaymentAmount <= 0m)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_AMOUNT_REQUIRED", "مبلغ تخصیص باید بزرگ‌تر از صفر باشد.");
        }

        var payment = await db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Id == request.PaymentTransactionId, ct)
            ?? throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_PAYMENT_NOT_FOUND", "دریافت انتخاب‌شده یافت نشد.");

        if (payment.PaymentKind != PaymentKind.CustomerReceipt || payment.Direction != PaymentDirection.In)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_NOT_CUSTOMER_RECEIPT", "فقط دریافت از مشتری قابل تخصیص است.");
        }

        var order = await db.PreSaleOrders
            .FirstOrDefaultAsync(o => o.Id == request.PreSaleOrderId, ct)
            ?? throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_PRESALE_NOT_FOUND", "پیش‌فروش یافت نشد.");

        if (payment.CustomerId != order.CustomerId)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_CUSTOMER_MISMATCH", "این دریافت متعلق به مشتری همین پیش‌فروش نیست.");
        }

        if (order.Status == PreSaleOrderStatus.Cancelled)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_PRESALE_CANCELLED", "پیش‌فروش لغوشده تخصیص پرداخت نمی‌پذیرد.");
        }

        // نرخ روز دریافت باید معتبر باشد؛ در غیر این صورت هیچ معادل دلاری قابل اتکایی وجود ندارد
        // و ساختن نرخ از روی نسبت مبالغ ممنوع است.
        var rate = payment.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_INVALID_FX",
                "این دریافت نرخ تبدیل معتبر ندارد و تا اصلاح نرخ قابل تخصیص نیست.");
        }

        if (SystemCurrency.IsBaseCurrency(payment.Currency) && rate.Value != 1m)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_INVALID_FX", "نرخ تبدیل دریافت دالری معتبر نیست.");
        }

        var allocatable = await GetAllocatablePaymentAmountAsync(payment.Id, ct);
        if (request.AllocatedPaymentAmount > allocatable + Epsilon)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_OVER_PAYMENT",
                $"مبلغ تخصیص از مانده قابل تخصیص این دریافت ({allocatable:N2} {payment.Currency}) بیشتر است.");
        }

        var amountUsd = decimal.Round(
            request.AllocatedPaymentAmount * rate.Value, 4, MidpointRounding.AwayFromZero);

        var remainingOrderUsd = await GetUnallocatedPreSaleAmountUsdAsync(order.Id, ct);
        if (amountUsd > remainingOrderUsd + Epsilon)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_OVER_PRESALE",
                $"مبلغ تخصیص از مانده مالی این پیش‌فروش ({remainingOrderUsd:N2} USD) بیشتر است.");
        }

        var allocation = new CustomerPaymentAllocation
        {
            PaymentTransactionId = payment.Id,
            PreSaleOrderId = order.Id,
            AllocationDate = request.AllocationDate == default
                ? AfghanistanBusinessClock.SystemToday
                : request.AllocationDate.Date,
            AllocatedPaymentAmount = request.AllocatedPaymentAmount,
            PaymentCurrencyCode = payment.Currency,
            PaymentFxRateToUsd = rate.Value,
            AllocatedAmountUsd = amountUsd,
            ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? null : request.ReferenceNumber.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Status = CustomerPaymentAllocationStatus.Active,
            CreatedByUserName = request.CreatedByUserName
        };

        db.CustomerPaymentAllocations.Add(allocation);
        await db.SaveChangesAsync(ct);

        return allocation;
    }

    public async Task<CustomerPaymentAllocation> ReverseAsync(
        CustomerPaymentAllocationReverseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var allocation = await db.CustomerPaymentAllocations
            .FirstOrDefaultAsync(a => a.Id == request.AllocationId, ct)
            ?? throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_NOT_FOUND", "تخصیص یافت نشد.");

        if (allocation.Status == CustomerPaymentAllocationStatus.Reversed)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_ALREADY_REVERSED", "این تخصیص قبلاً برگشت خورده است.");
        }

        // تخصیصی که روی تحویل مصرف شده مستقیم برگشت نمی‌خورد؛ ابتدا باید مصرفِ مربوطه (با لغو همان
        // تحویل) آزاد شود تا هیچ ژورنال، طلب یا پیش‌دریافتِ ناسازگاری باقی نماند.
        var hasActiveApplications = await db.CustomerPaymentAllocationApplications
            .AnyAsync(a => a.CustomerPaymentAllocationId == allocation.Id
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active, ct);
        if (hasActiveApplications)
        {
            throw new BusinessRuleException(
                "CUSTOMER_ALLOCATION_HAS_APPLICATIONS",
                "این تخصیص روی یک یا چند تحویل مصرف شده است؛ ابتدا آن تحویل‌ها را لغو کنید تا مصرف آزاد شود.");
        }

        // رکورد حذف نمی‌شود؛ فقط برگشت می‌خورد تا مانده دریافت دوباره آزاد شود.
        allocation.Status = CustomerPaymentAllocationStatus.Reversed;
        allocation.ReversedAtUtc = DateTime.UtcNow;
        allocation.ReversedByUserName = request.ReversedByUserName;
        allocation.ReversalReason = string.IsNullOrWhiteSpace(request.ReversalReason)
            ? null
            : request.ReversalReason.Trim();
        await db.SaveChangesAsync(ct);

        return allocation;
    }

    private async Task<decimal> GetAllocatedUsdAsync(int preSaleOrderId, CancellationToken ct)
        => await db.CustomerPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PreSaleOrderId == preSaleOrderId
                && a.Status == CustomerPaymentAllocationStatus.Active)
            .SumAsync(a => (decimal?)a.AllocatedAmountUsd, ct) ?? 0m;
}
