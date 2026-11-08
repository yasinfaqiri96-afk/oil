using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Notifications;

public sealed class UnofficialWhatsAppOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:8787";
    public string Secret { get; set; } = "";
}

// همهٔ متدها پس از Commit مالی صدا زده می‌شوند و هرگز خطا بیرون نمی‌دهند؛
// نبودِ sender یا شماره فقط log می‌شود و ثبت مالی دست نمی‌خورد.
public interface IUnofficialWhatsAppReceiptService
{
    Task QueuePaymentReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken = default);
    Task QueueSaleReceiptAsync(SalesTransaction sale, CancellationToken cancellationToken = default);
    Task QueueSaleReceiptAsync(int saleId, CancellationToken cancellationToken = default);
    Task QueueSaleBatchReceiptAsync(SalesBatch batch, CancellationToken cancellationToken = default);
    Task QueueCustomerTransferReceiptAsync(CustomerToCustomerPayment transfer, CancellationToken cancellationToken = default);
}

public sealed class UnofficialWhatsAppReceiptService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext db,
    IConfiguration configuration,
    ILogger<UnofficialWhatsAppReceiptService> logger) : IUnofficialWhatsAppReceiptService
{
    private sealed record Recipient(string? Name, string? Phone);

    public Task QueuePaymentReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken = default)
        => QueueAsync(payment.Id.ToString(), ResolvePaymentRecipient(payment),
            name => BuildPaymentText(payment, name), cancellationToken);

    public Task QueueSaleReceiptAsync(SalesTransaction sale, CancellationToken cancellationToken = default)
        => QueueAsync($"sale-{sale.Id}", ResolveCustomer(sale.CustomerId),
            name => BuildSaleText(sale, name), cancellationToken);

    public async Task QueueSaleReceiptAsync(int saleId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sale = await db.SalesTransactions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == saleId, cancellationToken);
            if (sale is not null) await QueueSaleReceiptAsync(sale, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WhatsApp receipt skipped for sale-{SaleId}.", saleId);
        }
    }

    public Task QueueSaleBatchReceiptAsync(SalesBatch batch, CancellationToken cancellationToken = default)
        => QueueAsync($"sale-batch-{batch.Id}", ResolveCustomer(batch.CustomerId),
            name => BuildSaleBatchText(batch, name), cancellationToken);

    public async Task QueueCustomerTransferReceiptAsync(CustomerToCustomerPayment transfer, CancellationToken cancellationToken = default)
    {
        // هر دو طرف انتقال مانده‌شان تغییر می‌کند؛ هر کدام رسید خودش را می‌گیرد.
        await QueueAsync($"c2c-{transfer.Id}-payer", ResolveCustomer(transfer.PayerCustomerId),
            name => BuildCustomerTransferText(transfer, name, isPayer: true), cancellationToken);
        await QueueAsync($"c2c-{transfer.Id}-payee", ResolveCustomer(transfer.PayeeCustomerId),
            name => BuildCustomerTransferText(transfer, name, isPayer: false), cancellationToken);
    }

    private async Task QueueAsync(string id, IQueryable<Recipient> recipientQuery, Func<string?, string> buildText,
        CancellationToken cancellationToken)
    {
        try
        {
            var enabled = configuration.GetValue<bool>("WhatsAppUnofficial:Enabled");
            var configuredSecret = configuration["WhatsAppUnofficial:Secret"];
            var secret = string.IsNullOrWhiteSpace(configuredSecret)
                ? Environment.GetEnvironmentVariable("WA_SENDER_SECRET")
                : configuredSecret;
            if (!enabled || string.IsNullOrWhiteSpace(secret)) return;

            var recipient = await recipientQuery.FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(recipient?.Phone))
            {
                logger.LogInformation("WhatsApp receipt skipped for {ReceiptId}: no phone number.", id);
                return;
            }

            var baseUrl = configuration["WhatsAppUnofficial:BaseUrl"] ?? "http://127.0.0.1:8787";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/send-receipt");
            request.Headers.Add("x-wa-sender-secret", secret);
            request.Content = JsonContent.Create(new { id, phone = recipient.Phone, text = buildText(recipient.Name) });
            using var response = await httpClientFactory.CreateClient("whatsapp-unofficial")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("WhatsApp receipt queue rejected {ReceiptId}: HTTP {StatusCode}.", id, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WhatsApp receipt queue unavailable for {ReceiptId}; financial transaction remains successful.", id);
        }
    }

    private IQueryable<Recipient> ResolvePaymentRecipient(PaymentTransaction payment)
    {
        if (payment.CustomerId.HasValue) return ResolveCustomer(payment.CustomerId);
        if (payment.SupplierId.HasValue) return db.Suppliers.Where(x => x.Id == payment.SupplierId).Select(x => new Recipient(x.Name, x.Phone));
        if (payment.ServiceProviderId.HasValue) return db.ServiceProviders.Where(x => x.Id == payment.ServiceProviderId).Select(x => new Recipient(x.Name, x.Phone));
        if (payment.SarrafId.HasValue) return db.Sarrafs.Where(x => x.Id == payment.SarrafId).Select(x => new Recipient(x.Name, x.PhoneNumber));
        if (payment.DriverId.HasValue) return db.Drivers.Where(x => x.Id == payment.DriverId).Select(x => new Recipient(x.FullName, x.Phone));
        if (payment.EmployeeId.HasValue) return db.Employees.Where(x => x.Id == payment.EmployeeId).Select(x => new Recipient(x.FullName, x.Phone));
        if (payment.PaidByPartnerId.HasValue) return db.Partners.Where(x => x.Id == payment.PaidByPartnerId).Select(x => new Recipient(x.Name, x.Phone));
        return db.Customers.Where(x => false).Select(x => new Recipient(x.Name, x.Phone));
    }

    private IQueryable<Recipient> ResolveCustomer(int? customerId)
        => db.Customers.Where(x => customerId.HasValue && x.Id == customerId).Select(x => new Recipient(x.Name, x.Phone));

    private static string Greeting(string? name)
        => string.IsNullOrWhiteSpace(name) ? "" : $"{name} گرامی\n";

    private static string BuildPaymentText(PaymentTransaction p, string? name)
        => $"{Greeting(name)}{(p.Direction == PaymentDirection.In ? "رسید دریافت وجه" : "رسید پرداخت وجه")} #{p.Id}\n"
            + $"تاریخ: {p.PaymentDate:yyyy-MM-dd}\nمبلغ: {p.Amount:N2} {p.Currency}\n"
            + $"مرجع: {p.Reference ?? "-"}\nتوضیحات: {p.Description ?? "-"}";

    private static string BuildSaleText(SalesTransaction s, string? name)
        => $"{Greeting(name)}رسید فروش #{s.Id}\nفاکتور: {s.InvoiceNumber}\nتاریخ: {s.SaleDate:yyyy-MM-dd}\n"
            + $"مقدار: {s.QuantityMt:N4} تن\n"
            + (s.IsSalePricePending ? "مبلغ: قیمت هنوز تعیین نشده است" : $"مبلغ: {s.TotalInCurrency:N2} {s.Currency}");

    private static string BuildSaleBatchText(SalesBatch b, string? name)
        => $"{Greeting(name)}رسید فروش گروهی {b.BatchNumber}\nتاریخ: {b.SaleDate:yyyy-MM-dd}\n"
            + $"تعداد ردیف: {b.LineCount}\nمقدار کل: {b.TotalQuantityMt:N4} تن\n"
            + (b.TotalInCurrency > 0m ? $"مبلغ کل: {b.TotalInCurrency:N2} {b.Currency}" : "مبلغ: قیمت هنوز تعیین نشده است");

    private static string BuildCustomerTransferText(CustomerToCustomerPayment t, string? name, bool isPayer)
        => $"{Greeting(name)}{(isPayer ? "رسید پرداخت به مشتری دیگر" : "رسید دریافت از مشتری دیگر")} #{t.Id}\n"
            + $"تاریخ: {t.PaymentDate:yyyy-MM-dd}\nمبلغ: {t.Amount:N2} {t.Currency}\nمرجع: {t.Reference ?? "-"}";
}
