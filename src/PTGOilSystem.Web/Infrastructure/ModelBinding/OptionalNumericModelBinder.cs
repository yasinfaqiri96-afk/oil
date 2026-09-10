using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace PTGOilSystem.Web.Infrastructure.ModelBinding;

/// <summary>
/// فرم‌های «ثبت جدید» دیگر صفرِ پیش‌فرض را داخل input نمی‌گذارند (EmptyNumericInputTagHelper)،
/// بنابراین یک فیلد عددیِ اختیاری می‌تواند خالی post شود. binder پیش‌فرض برای نوع مقداریِ
/// non-nullable خطای «The value '' is invalid» می‌دهد و فرم را رد می‌کند.
///
/// این binder فقط همان حالت را پوشش می‌دهد: مقدارِ کاملاً خالی برای یک عددِ non-nullable که
/// <see cref="RequiredAttribute"/> صریح ندارد، به مقدار پیش‌فرض (صفر) bind می‌شود؛ دقیقاً همان
/// چیزی که پیش از این با نمایش صفر در فرم اتفاق می‌افتاد.
///
/// فیلدهای دارای [Required] عمداً از این مسیر خارج‌اند تا validation استاندارد «مقدار الزامی است»
/// حفظ شود. ورودیِ نامعتبر (مثلاً «abc») هم مثل قبل خطای parse می‌گیرد.
/// </summary>
public sealed class OptionalNumericModelBinder : IModelBinder
{
    private readonly IModelBinder _inner;
    private readonly object _defaultValue;

    public OptionalNumericModelBinder(IModelBinder inner, Type modelType)
    {
        _inner = inner;
        _defaultValue = Activator.CreateInstance(modelType)!;
    }

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (valueResult != ValueProviderResult.None && string.IsNullOrWhiteSpace(valueResult.FirstValue))
        {
            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
            bindingContext.Result = ModelBindingResult.Success(_defaultValue);
            return Task.CompletedTask;
        }

        return _inner.BindModelAsync(bindingContext);
    }
}

public sealed class OptionalNumericModelBinderProvider : IModelBinderProvider
{
    private static readonly HashSet<Type> NumericTypes =
    [
        typeof(decimal), typeof(double), typeof(float),
        typeof(int), typeof(long), typeof(short),
        typeof(byte), typeof(sbyte),
        typeof(uint), typeof(ulong), typeof(ushort)
    ];

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // binder صریح روی پراپرتی، اولویت دارد.
        if (context.BindingInfo.BinderType is not null)
            return null;

        var modelType = context.Metadata.ModelType;
        if (Nullable.GetUnderlyingType(modelType) is not null)
            return null; // nullable خودش خالی را null می‌کند.

        if (!NumericTypes.Contains(modelType))
            return null;

        if (HasExplicitRequired(context.Metadata))
            return null; // validation «مقدار الزامی است» باید سر جایش بماند.

        var loggerFactory = (ILoggerFactory)context.Services.GetService(typeof(ILoggerFactory))!;
        return new OptionalNumericModelBinder(new SimpleTypeModelBinder(modelType, loggerFactory), modelType);
    }

    private static bool HasExplicitRequired(ModelMetadata metadata)
        => metadata is DefaultModelMetadata defaultMetadata
           && defaultMetadata.Attributes.Attributes.OfType<RequiredAttribute>().Any();
}
