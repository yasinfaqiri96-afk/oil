using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PTG.ContractReportingRepair;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Reporting;
[assembly: HostingStartup(typeof(PTG.DirectCogsMaintenance.MaintenanceStartup))]
namespace PTG.DirectCogsMaintenance;
/// <summary>Explicit one-shot repair tool. Never copied into the public production release.</summary>
public sealed class MaintenanceStartup : IHostingStartup {
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services => services.AddHostedService<RepairWorker>());
}
public sealed class RepairWorker(IServiceProvider provider, IConfiguration config, IHostApplicationLifetime lifetime) : IHostedService {
    public async Task StartAsync(CancellationToken ct) {
        if (config["PTG_DIRECT_COGS_REPAIR"] != "INV003-P002-98000")
            throw new InvalidOperationException("Explicit scoped repair activation is required.");
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conn = db.Database.GetConnectionString()!;
        if (!conn.Contains("Database=ptg_zuri_demo;", StringComparison.Ordinal)
            && !conn.Contains("Database=zuri_p002_repair_test;", StringComparison.Ordinal))
            throw new InvalidOperationException("This maintenance tool cannot run against another database.");
        var adapter = scope.ServiceProvider.GetRequiredService<ISalesAccountingAdapter>();
        if (adapter is not DirectTransportSalesAccountingAdapter) throw new InvalidOperationException("Direct COGS adapter is not installed.");
        var sale = await db.SalesTransactions.SingleAsync(s => s.Id == 3, ct);
        if (sale.InvoiceNumber != "INV003" || sale.SourcePurchaseContractId != 2 || sale.QuantityMt != 98m
            || sale.TotalUsd != 147000m || sale.IsCancelled)
            throw new InvalidOperationException("Reviewed sale changed; repair refused.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await adapter.TryPostCogsAsync(sale, ct);
        if (result.Status is not (PaymentPostingStatus.Posted or PaymentPostingStatus.Duplicate)
            || result.Journal?.Lines.Sum(l => l.Debit) != 98000m)
            throw new InvalidOperationException("Reviewed direct cost was not posted: " + result.Reason);
        var pnl = await scope.ServiceProvider.GetRequiredService<IProfitAndLossService>().BuildForSalesAsync([3,4], ct);
        if (pnl.UncostedSaleCount != 0 || pnl.CostOfGoodsSoldUsd != 197500m || pnl.GrossProfitUsd != 98750m)
            throw new InvalidOperationException("Canonical P&L verification failed.");
        await transaction.CommitAsync(ct);
        Console.WriteLine("DIRECT_COGS_REPAIR " + System.Text.Json.JsonSerializer.Serialize(new {
            Status = result.Status.ToString(), result.Journal.Id, CostUsd = 98000m,
            TotalCogsUsd = pnl.CostOfGoodsSoldUsd, GrossProfitUsd = pnl.GrossProfitUsd, pnl.UncostedSaleCount
        }));
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
