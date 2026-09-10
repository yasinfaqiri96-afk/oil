using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Infrastructure.ModelBinding;
using PTGOilSystem.Web.TagHelpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فرم «ثبت جدید» نباید صفرِ پیش‌فرض را داخل input بگذارد، ولی Edit/Details باید صفرِ واقعی
/// رکورد را نشان بدهد و فیلد عددیِ اختیاریِ خالی هم باید بدون خطا bind شود.
/// </summary>
public class EmptyNumericInputTests
{
    private sealed class SampleForm
    {
        public decimal QuantityMt { get; set; }
        public decimal? RateUsd { get; set; }
        public int Count { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class RequiredForm
    {
        [Required]
        public decimal AmountUsd { get; set; }

        public decimal FreightUsd { get; set; }
    }

    // ---- TagHelper -----------------------------------------------------------

    [Fact]
    public void CreateForm_ZeroDecimal_RendersEmptyInput()
    {
        Assert.Null(RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Create"));
    }

    [Fact]
    public void CreateForm_ZeroInt_RendersEmptyInput()
    {
        Assert.Null(RenderInput(new SampleForm(), nameof(SampleForm.Count), "Create"));
    }

    [Fact]
    public void CreateForm_NonZeroValue_IsPreserved()
    {
        var value = RenderInput(new SampleForm { QuantityMt = 12.5m }, nameof(SampleForm.QuantityMt), "Create");
        Assert.Equal("12.50", value);
    }

    [Fact]
    public void EditForm_RealZero_IsStillShown()
    {
        Assert.Equal("0.00", RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Edit"));
    }

    [Fact]
    public void DetailsForm_RealZero_IsStillShown()
    {
        Assert.Equal("0.00", RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Details"));
    }

    [Fact]
    public void CreateForm_HiddenInput_KeepsZero()
    {
        var value = RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Create", inputType: "hidden");
        Assert.Equal("0.00", value);
    }

    [Fact]
    public void CreateForm_ReadonlyInput_KeepsZero()
    {
        var value = RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Create", extraAttribute: "readonly");
        Assert.Equal("0.00", value);
    }

    [Fact]
    public void CreateForm_KeepZeroOptOut_KeepsZero()
    {
        var value = RenderInput(new SampleForm(), nameof(SampleForm.QuantityMt), "Create", keepZero: true);
        Assert.Equal("0.00", value);
    }

    [Fact]
    public void CreateForm_UserTypedZero_IsPreservedAfterValidationError()
    {
        var value = RenderInput(
            new SampleForm(),
            nameof(SampleForm.QuantityMt),
            "Create",
            seedModelState: state => state.SetModelValue(
                nameof(SampleForm.QuantityMt),
                new ValueProviderResult("0")));

        Assert.Equal("0", value);
    }

    [Fact]
    public void CreateForm_NonNumericField_IsUntouched()
    {
        var value = RenderInput(new SampleForm { Notes = "0" }, nameof(SampleForm.Notes), "Create", inputType: "text");
        Assert.Equal("0", value);
    }

    [Fact]
    public void CreateForm_PlainNumberInputWithoutAspFor_LosesZero()
    {
        var services = BuildServices();
        var viewContext = BuildViewContext(services, new SampleForm(), "Create");
        var attributes = new TagHelperAttributeList
        {
            new TagHelperAttribute("type", "number"),
            new TagHelperAttribute("name", "Items[0].Amount"),
            new TagHelperAttribute("value", "0")
        };

        var output = RunTagHelpers(viewContext, attributes, expression: null);
        Assert.Null(output.Attributes["value"]);
    }

    // ---- Model binding -------------------------------------------------------

    [Fact]
    public void EmptyOptionalNumeric_BindsToZeroWithoutError()
    {
        var services = BuildServices();
        var metadata = MetadataFor(services, typeof(RequiredForm), nameof(RequiredForm.FreightUsd));
        var binder = new OptionalNumericModelBinderProvider()
            .GetBinder(new TestBinderProviderContext(metadata, services));

        Assert.NotNull(binder);

        var bindingContext = BuildBindingContext(services, metadata, nameof(RequiredForm.FreightUsd), string.Empty);
        binder!.BindModelAsync(bindingContext).GetAwaiter().GetResult();

        Assert.True(bindingContext.Result.IsModelSet);
        Assert.Equal(0m, bindingContext.Result.Model);
        Assert.Equal(0, bindingContext.ModelState.ErrorCount);
    }

    [Fact]
    public void TypedNumber_StillBindsNormally()
    {
        var services = BuildServices();
        var metadata = MetadataFor(services, typeof(RequiredForm), nameof(RequiredForm.FreightUsd));
        var binder = new OptionalNumericModelBinderProvider()
            .GetBinder(new TestBinderProviderContext(metadata, services));

        var bindingContext = BuildBindingContext(services, metadata, nameof(RequiredForm.FreightUsd), "12.5");
        binder!.BindModelAsync(bindingContext).GetAwaiter().GetResult();

        Assert.Equal(12.5m, bindingContext.Result.Model);
        Assert.Equal(0, bindingContext.ModelState.ErrorCount);
    }

    [Fact]
    public void ExplicitRequiredNumeric_KeepsStandardRequiredValidation()
    {
        var services = BuildServices();
        var metadata = MetadataFor(services, typeof(RequiredForm), nameof(RequiredForm.AmountUsd));

        Assert.Null(new OptionalNumericModelBinderProvider()
            .GetBinder(new TestBinderProviderContext(metadata, services)));
    }

    [Fact]
    public void NullableNumeric_IsLeftToDefaultBinder()
    {
        var services = BuildServices();
        var metadata = MetadataFor(services, typeof(SampleForm), nameof(SampleForm.RateUsd));

        Assert.Null(new OptionalNumericModelBinderProvider()
            .GetBinder(new TestBinderProviderContext(metadata, services)));
    }

    // ---- helpers -------------------------------------------------------------

    private static string? RenderInput(
        object model,
        string propertyName,
        string action,
        string inputType = "number",
        string? extraAttribute = null,
        bool keepZero = false,
        Action<ModelStateDictionary>? seedModelState = null)
    {
        var services = BuildServices();
        var viewContext = BuildViewContext(services, model, action);
        seedModelState?.Invoke(viewContext.ViewData.ModelState);

        var attributes = new TagHelperAttributeList { new TagHelperAttribute("type", inputType) };
        if (extraAttribute is not null)
        {
            attributes.Add(new TagHelperAttribute(extraAttribute));
        }

        if (keepZero)
        {
            attributes.Add(new TagHelperAttribute("data-keep-zero", "true"));
        }

        var explorer = viewContext.ViewData.ModelExplorer.GetExplorerForProperty(propertyName);
        var expression = new ModelExpression(propertyName, explorer);

        var output = RunTagHelpers(viewContext, attributes, expression);
        return output.Attributes["value"]?.Value?.ToString();
    }

    private static TagHelperOutput RunTagHelpers(
        ViewContext viewContext,
        TagHelperAttributeList attributes,
        ModelExpression? expression)
    {
        var context = new TagHelperContext(attributes, new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput(
            "input",
            new TagHelperAttributeList(attributes),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        if (expression is not null)
        {
            var generator = viewContext.HttpContext.RequestServices.GetRequiredService<IHtmlGenerator>();
            var inputTagHelper = new InputTagHelper(generator) { For = expression, ViewContext = viewContext };
            inputTagHelper.Process(context, output);
        }

        new EmptyNumericInputTagHelper { For = expression, ViewContext = viewContext }.Process(context, output);
        return output;
    }

    private static ModelMetadata MetadataFor(IServiceProvider services, Type containerType, string propertyName)
        => services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForProperties(containerType)
            .Single(metadata => metadata.PropertyName == propertyName);

    private static DefaultModelBindingContext BuildBindingContext(
        IServiceProvider services,
        ModelMetadata metadata,
        string modelName,
        string value)
    {
        return new DefaultModelBindingContext
        {
            ModelMetadata = metadata,
            ModelName = modelName,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new SimpleValueProvider { { modelName, value } },
            ActionContext = new ActionContext(
                new DefaultHttpContext { RequestServices = services },
                new RouteData(),
                new ActionDescriptor())
        };
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddDataProtection();
        services.AddAntiforgery();
        services.AddMvcCore().AddViews().AddDataAnnotations();
        return services.BuildServiceProvider();
    }

    private static ViewContext BuildViewContext(IServiceProvider services, object model, string action)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Values["action"] = action;

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var metadataProvider = services.GetRequiredService<IModelMetadataProvider>();
        var viewData = new ViewDataDictionary(metadataProvider, actionContext.ModelState) { Model = model };

        return new ViewContext(
            actionContext,
            new StubView(),
            viewData,
            new TempDataDictionary(httpContext, new StubTempDataProvider()),
            TextWriter.Null,
            new HtmlHelperOptions());
    }

    private sealed class StubView : IView
    {
        public string Path => "/stub";

        public Task RenderAsync(ViewContext context) => Task.CompletedTask;
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class SimpleValueProvider : Dictionary<string, string>, IValueProvider
    {
        public bool ContainsPrefix(string prefix) => ContainsKey(prefix);

        public ValueProviderResult GetValue(string key)
            => TryGetValue(key, out var value) ? new ValueProviderResult(value) : ValueProviderResult.None;
    }

    private sealed class TestBinderProviderContext : ModelBinderProviderContext
    {
        public TestBinderProviderContext(ModelMetadata metadata, IServiceProvider services)
        {
            Metadata = metadata;
            Services = services;
            MetadataProvider = services.GetRequiredService<IModelMetadataProvider>();
        }

        public override BindingInfo BindingInfo { get; } = new();

        public override ModelMetadata Metadata { get; }

        public override IModelMetadataProvider MetadataProvider { get; }

        public override IServiceProvider Services { get; }

        public override IModelBinder CreateBinder(ModelMetadata metadata)
            => throw new NotSupportedException();
    }
}
