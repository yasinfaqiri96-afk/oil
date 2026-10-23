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

public interface IUnofficialWhatsAppReceiptService
{
    Task QueuePaymentReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken = default);
    Task QueueSaleReceiptAsync(SalesTransaction sale, CancellationToken cancellationToken = default);
}

public sealed class UnofficialWhatsAppReceiptService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext db,
    IConfiguration configuration,
    ILogger<UnofficialWhatsAppReceiptService> logger) : IUnofficialWhatsAppReceiptService
{
    public Task QueuePaymentReceiptAsync(PaymentTransaction payment, CancellationToken cancellationToken = default)
        => QueueAsync(payment.Id.ToString(), ResolvePhoneQuery(payment), BuildPaymentText(payment), cancellationToken);

    public Task QueueSaleReceiptAsync(SalesTransaction sale, CancellationToken cancellationToken = default)
        => QueueAsync($"sale-{sale.Id}", ResolveSalePhoneQuery(sale), BuildSaleText(sale), cancellationToken);

    private async Task QueueAsync(string id, IQueryable<string?> phoneQuery, string text, CancellationToken cancellationToken)
    {
        var enabled = configuration.GetValue<bool>("WhatsAppUnofficial:Enabled");
        var configuredSecret = configuration["WhatsAppUnofficial:Secret"];
        var secret = string.IsNullOrWhiteSpace(configuredSecret)
            ? Environment.GetEnvironmentVariable("WA_SENDER_SECRET")
            : configuredSecret;
        if (!enabled || string.IsNullOrWhiteSpace(secret)) return;

        var phone = await phoneQuery.FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(phone))
        {
            logger.LogInformation("WhatsApp receipt skipped for {ReceiptId}: no phone number.", id);
            return;
        }

        var baseUrl = configuration["WhatsAppUnofficial:BaseUrl"] ?? "http://127.0.0.1:8787";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/send-receipt");
            request.Headers.Add("x-wa-sender-secret", secret);
            request.Content = JsonContent.Create(new { id, phone, text });
            var response = await httpClientFactory.CreateClient("whatsapp-unofficial")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("WhatsApp receipt queue rejected {ReceiptId}: HTTP {StatusCode}.", id, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WhatsApp receipt queue unavailable for {ReceiptId}; financial transaction remains successful.", id);
        }
    }

    private IQueryable<string?> ResolvePhoneQuery(PaymentTransaction payment)
    {
        if (payment.CustomerId.HasValue) return db.Customers.Where(x => x.Id == payment.CustomerId).Select(x => x.Phone);
        if (payment.SupplierId.HasValue) return db.Suppliers.Where(x => x.Id == payment.SupplierId).Select(x => x.Phone);
        if (payment.ServiceProviderId.HasValue) return db.ServiceProviders.Where(x => x.Id == payment.ServiceProviderId).Select(x => x.Phone);
        if (payment.PaidByPartnerId.HasValue) return db.Partners.Where(x => x.Id == payment.PaidByPartnerId).Select(x => x.Phone);
        return db.Customers.Where(x => false).Select(x => x.Phone);
    }

    private IQueryable<string?> ResolveSalePhoneQuery(SalesTransaction sale)
        => sale.CustomerId.HasValue
            ? db.Customers.Where(x => x.Id == sale.CustomerId).Select(x => x.Phone)
            : db.Customers.Where(x => false).Select(x => x.Phone);

    private static string BuildPaymentText(PaymentTransaction p)
        => $"رسید تراکنش #{p.Id}\nتاریخ: {p.PaymentDate:yyyy-MM-dd}\nمبلغ: {p.Amount:N2} {p.Currency}\nمرجع: {p.Reference ?? "-"}\nتوضیحات: {p.Description ?? "-"}";

    private static string BuildSaleText(SalesTransaction s)
        => $"رسید فروش #{s.Id}\nفاکتور: {s.InvoiceNumber}\nتاریخ: {s.SaleDate:yyyy-MM-dd}\nمقدار: {s.QuantityMt:N4}\nمبلغ: {s.TotalInCurrency:N2} {s.Currency}";
}
