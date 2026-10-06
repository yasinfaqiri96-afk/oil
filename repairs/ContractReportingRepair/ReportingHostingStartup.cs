using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Models.ContractJourney;

[assembly: HostingStartup(typeof(PTG.ContractReportingRepair.ReportingHostingStartup))]
namespace PTG.ContractReportingRepair;

public sealed class ReportingHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        services.AddScoped<ContractReportRepairService>();
        services.AddScoped<ContractReportRepairFilter>();
        services.AddControllersWithViews(options => options.Filters.AddService<ContractReportRepairFilter>())
            .AddApplicationPart(typeof(ReportingHostingStartup).Assembly);
    });
}

public sealed class ContractReportRepairFilter(ContractReportRepairService reporting) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (string.Equals(context.RouteData.Values["controller"]?.ToString(), "ContractJourney", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.RouteData.Values["action"]?.ToString(), "Details", StringComparison.OrdinalIgnoreCase)
            && context.Result is ViewResult view && view.Model is ContractJourneyDetailsViewModel model
            && model.IsPurchaseContract && model.SubContractItems.Count == 0)
        {
            var report = await reporting.ReadAsync(model, context.HttpContext.RequestAborted);
            view.ViewData["ContractReportRepair"] = report;
            view.ViewName = "/Views/ContractJourneyRepair/Details.cshtml";
        }
        await next();
    }
}
