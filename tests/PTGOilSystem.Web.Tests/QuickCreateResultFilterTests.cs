using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ServiceProviders;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.QuickCreate;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class QuickCreateResultFilterTests
{
    [Fact]
    public async Task Quick_Create_Redirect_Becomes_Json_With_New_Item()
    {
        await using var db = CreateDb();
        db.Customers.Add(new Customer { Name = "Existing" });
        await db.SaveChangesAsync();

        var (executing, filters, actionContext) = CreateContext(
            controller: "Customers",
            quickCreate: true,
            db: db);
        var created = new Customer { Name = "Modal Customer" };
        ActionExecutedContext? executed = null;

        await new QuickCreateResultFilter().OnActionExecutionAsync(executing, async () =>
        {
            db.Customers.Add(created);
            await db.SaveChangesAsync();
            executed = new ActionExecutedContext(actionContext, filters, new object())
            {
                Result = new RedirectToActionResult("Index", "Customers", null)
            };
            return executed;
        });

        Assert.NotNull(executed);
        var json = Assert.IsType<JsonResult>(executed.Result);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(json.Value));
        Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(created.Id.ToString(), payload.RootElement.GetProperty("item").GetProperty("id").GetString());
        Assert.Equal("Modal Customer", payload.RootElement.GetProperty("item").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Normal_Create_Keeps_Existing_Redirect()
    {
        await using var db = CreateDb();
        var (executing, filters, actionContext) = CreateContext(
            controller: "Customers",
            quickCreate: false,
            db: db);
        ActionExecutedContext? executed = null;

        await new QuickCreateResultFilter().OnActionExecutionAsync(executing, () =>
        {
            executed = new ActionExecutedContext(actionContext, filters, new object())
            {
                Result = new RedirectToActionResult("Index", "Customers", null)
            };
            return Task.FromResult(executed);
        });

        Assert.NotNull(executed);
        Assert.IsType<RedirectToActionResult>(executed.Result);
    }

    [Fact]
    public async Task Quick_Create_Validation_Keeps_View_Result_In_Modal()
    {
        await using var db = CreateDb();
        var (executing, filters, actionContext) = CreateContext(
            controller: "Drivers",
            quickCreate: true,
            db: db);
        var invalidModel = new Driver();
        ActionExecutedContext? executed = null;

        await new QuickCreateResultFilter().OnActionExecutionAsync(executing, () =>
        {
            executed = new ActionExecutedContext(actionContext, filters, new object())
            {
                Result = new ViewResult { ViewName = "Create" }
            };
            return Task.FromResult(executed);
        });

        Assert.NotNull(executed);
        Assert.IsType<ViewResult>(executed.Result);
        Assert.Equal(0, invalidModel.Id);
    }

    [Fact]
    public void Currency_Definition_Returns_Code_Value_And_Compound_Label()
    {
        var definition = Assert.IsType<QuickCreateEntityDefinition>(
            QuickCreateEntityRegistry.ForController("Currencies"));
        var currency = new Currency { Id = 9, Code = "EUR", Name = "Euro" };

        Assert.Equal("EUR", QuickCreateEntityRegistry.ReadString(currency, "Code"));
        Assert.Equal("EUR - Euro", definition.BuildLabel(currency));
    }

    [Theory]
    [InlineData("Customers", "Quick Customer")]
    [InlineData("Suppliers", "Quick Supplier")]
    [InlineData("ServiceProviders", "Quick Responsible")]
    [InlineData("Drivers", "Quick Driver")]
    [InlineData("Trucks", "QK-101")]
    public async Task Existing_Master_Data_Create_Actions_Return_Selectable_Quick_Create_Item(
        string controllerName,
        string expectedLabel)
    {
        await using var db = CreateDb();
        var controller = BuildController(controllerName, db);
        var (executing, filters, actionContext) = CreateContext(
            controller: controllerName,
            quickCreate: true,
            db: db,
            controllerInstance: controller);
        ActionExecutedContext? executed = null;

        await new QuickCreateResultFilter().OnActionExecutionAsync(executing, async () =>
        {
            var result = controllerName switch
            {
                "Customers" => await ((CustomersController)controller).Create(
                    new Customer { Name = expectedLabel, IsActive = true }),
                "Suppliers" => await ((SuppliersController)controller).Create(
                    new Supplier { Name = expectedLabel, IsActive = true }),
                "ServiceProviders" => await ((ServiceProvidersController)controller).Create(
                    new ServiceProviderCreateViewModel { Name = expectedLabel, IsActive = true }),
                "Drivers" => await ((DriversController)controller).Create(
                    new Driver { FullName = expectedLabel, IsActive = true }),
                "Trucks" => await ((TrucksController)controller).Create(
                    new Truck { PlateNumber = expectedLabel, IsActive = true }),
                _ => throw new InvalidOperationException(controllerName)
            };

            executed = new ActionExecutedContext(actionContext, filters, controller)
            {
                Result = result
            };
            return executed;
        });

        Assert.NotNull(executed);
        var json = Assert.IsType<JsonResult>(executed.Result);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(json.Value));
        Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(expectedLabel, payload.RootElement.GetProperty("item").GetProperty("label").GetString());
        Assert.NotEqual("0", payload.RootElement.GetProperty("item").GetProperty("id").GetString());
    }

    private static (
        ActionExecutingContext Executing,
        IList<IFilterMetadata> Filters,
        ActionContext ActionContext) CreateContext(
            string controller,
            bool quickCreate,
            ApplicationDbContext db,
            object? controllerInstance = null)
    {
        var httpContext = new DefaultHttpContext
        {
            // فیلتر، DbContext را تنبل از RequestServices می‌گیرد (همان نمونهٔ scoped).
            RequestServices = new SingleServiceProvider(db)
        };
        httpContext.Request.Method = HttpMethods.Post;
        if (quickCreate)
        {
            httpContext.Request.QueryString = new QueryString("?modal=1&quickCreate=1");
            httpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        }

        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = "Create";
        var actionContext = new ActionContext(
            httpContext,
            routeData,
            new ActionDescriptor(),
            new ModelStateDictionary());
        IList<IFilterMetadata> filters = [];
        var executing = new ActionExecutingContext(
            actionContext,
            filters,
            new Dictionary<string, object?>(),
            controllerInstance ?? new object());
        return (executing, filters, actionContext);
    }

    private static Controller BuildController(string controllerName, ApplicationDbContext db)
    {
        Controller controller = controllerName switch
        {
            "Customers" => new CustomersController(
                db,
                new AuditService(db),
                new MasterDataDeleteSafetyService(db)),
            "Suppliers" => new SuppliersController(
                db,
                new AuditService(db),
                new MasterDataDeleteSafetyService(db)),
            "ServiceProviders" => new ServiceProvidersController(db),
            "Drivers" => new DriversController(db, new AuditService(db)),
            "Trucks" => new TrucksController(db, new AuditService(db)),
            _ => throw new InvalidOperationException(controllerName)
        };
        controller.TempData = new TempDataDictionary(
            new DefaultHttpContext(),
            new InMemoryTempDataProvider());
        return controller;
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class SingleServiceProvider(ApplicationDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? db : null;
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
