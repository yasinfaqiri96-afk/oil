using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using PTGOilSystem.Web.Security;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «راهنمای هوشمند» کلید ناوبری ندارد، پس فیلتر ناوبری آن را برای همه — حتی Admin —
/// به AccessDenied می‌فرستاد و پاسخ ۳۰۲ باعث شکستن fetch در Frontend می‌شد.
/// </summary>
public class AssistantNavigationFilterTests
{
    private static ActionExecutingContext BuildContext(string controller, ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static ClaimsPrincipal SignedInUser(string role) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role) },
        authenticationType: "TestAuth"));

    [Theory]
    [InlineData(AuthRoles.Admin)]
    [InlineData(AuthRoles.Viewer)]
    public async Task The_Assistant_Endpoint_Is_Not_Redirected_By_The_Navigation_Filter(string role)
    {
        var filter = new RoleNavigationAuthorizationFilter();
        var context = BuildContext("Assistant", SignedInUser(role));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });

        Assert.True(nextWasCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task An_Unknown_Controller_Is_Still_Redirected_To_Access_Denied()
    {
        var filter = new RoleNavigationAuthorizationFilter();
        var context = BuildContext("SomeUnknownController", SignedInUser(AuthRoles.Viewer));

        await filter.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object())));

        var redirect = Assert.IsType<RedirectToActionResult>(context.Result);
        Assert.Equal("AccessDenied", redirect.ActionName);
    }
}
