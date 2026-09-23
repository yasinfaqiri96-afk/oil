using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using PTGOilSystem.Web.Filters;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// در فهرست‌ها <c>page &lt;= 0</c> یعنی «بدون صفحه‌بندی»؛ این قرارداد فقط برای اکشن‌های
/// Export است که Index را مستقیم صدا می‌زنند. این تست قفل می‌کند که همان مقدار از سمت
/// درخواست کاربر به بازهٔ مجاز برگردد و صفحه‌بندی دور زده نشود.
/// </summary>
public sealed class ListPageBoundsFilterTests
{
    private static ActionExecutingContext NewContext(IDictionary<string, object?> arguments)
    {
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ControllerActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            arguments,
            controller: new object());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Non_Positive_Page_From_The_Request_Is_Clamped_To_The_First_Page(int page)
    {
        var context = NewContext(new Dictionary<string, object?> { ["page"] = page });

        new ListPageBoundsFilter().OnActionExecuting(context);

        Assert.Equal(1, context.ActionArguments["page"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void A_Real_Page_Number_Is_Left_Alone(int page)
    {
        var context = NewContext(new Dictionary<string, object?> { ["page"] = page });

        new ListPageBoundsFilter().OnActionExecuting(context);

        Assert.Equal(page, context.ActionArguments["page"]);
    }

    [Fact]
    public void An_Action_Without_A_Page_Argument_Is_Untouched()
    {
        var context = NewContext(new Dictionary<string, object?> { ["id"] = 5 });

        new ListPageBoundsFilter().OnActionExecuting(context);

        Assert.False(context.ActionArguments.ContainsKey("page"));
        Assert.Equal(5, context.ActionArguments["id"]);
    }
}
