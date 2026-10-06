using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
[assembly: HostingStartup(typeof(PTG.ContractReportingRepair.Tests.TestHostingStartup))]
namespace PTG.ContractReportingRepair.Tests;
public sealed class TestHostingStartup : IHostingStartup {
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices((context, services) => {
        if (!context.Configuration.GetConnectionString("DefaultConnection")!.Contains("Database=zuri_p002_repair_test;", StringComparison.Ordinal))
            throw new InvalidOperationException("Test authentication must only use the isolated test database.");
        services.AddHostedService<DirectCogsRegressionWorker>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestHandler>("ReportingTests", _ => {});
        services.PostConfigure<AuthenticationOptions>(options => {
            options.DefaultAuthenticateScheme = "ReportingTests";
            options.DefaultChallengeScheme = "ReportingTests";
        });
    });
}
public sealed class TestHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder) {
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier,"1"), new Claim(ClaimTypes.Name,"Reporting test"),
            new Claim(ClaimTypes.Role,"Admin") };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
    }
}
