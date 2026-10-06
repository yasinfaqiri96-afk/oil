using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Reporting;
using Microsoft.AspNetCore.Mvc;
namespace PTG.ContractReportingRepair.Tests;
public sealed class DirectCogsRegressionWorker(IServiceProvider provider) : IHostedService {
    public async Task StartAsync(CancellationToken ct) {
        using var scope = provider.CreateScope();
        var runner = new DirectCogsTestController(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<ISalesAccountingAdapter>(),
            scope.ServiceProvider.GetRequiredService<IProfitAndLossService>());
        var result = await runner.Run();
        if (result is not OkObjectResult ok) throw new Exception("Test runner refused isolated database.");
        Console.WriteLine("DIRECT_COGS_REGRESSION " + System.Text.Json.JsonSerializer.Serialize(ok.Value));
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
