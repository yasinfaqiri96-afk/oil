using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Controllers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class CompiledViewPackagingTests
{
    [Fact]
    public void Mvc_Discovers_Precompiled_Views_From_The_Web_Build_Output()
    {
        // Build the MVC part catalogue only; never execute Program/startup or
        // connect to a database. Works with combined and separate Razor output.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SalesController).Assembly.GetName().Name
        });
        var mvc = builder.Services.AddControllersWithViews();
        var feature = new ViewsFeature();
        mvc.PartManager.PopulateFeature(feature);
        var paths = feature.ViewDescriptors.Select(view => view.RelativePath).ToArray();
        Assert.Contains("/Views/Shared/_Layout.cshtml", paths);
        Assert.Contains("/Views/Loading/Create.cshtml", paths);
        Assert.Contains("/Views/Sales/Create.cshtml", paths);
    }
}
