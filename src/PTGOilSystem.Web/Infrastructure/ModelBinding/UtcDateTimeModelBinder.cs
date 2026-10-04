using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using PTGOilSystem.Web.Services.Calendars;

namespace PTGOilSystem.Web.Infrastructure.ModelBinding;

/// <summary>
/// تاریخ‌هایی که از query string یا فرم می‌آیند Kind=Unspecified دارند و Npgsql آن‌ها را
/// برای ستون‌های «timestamp with time zone» رد می‌کند. این binder همان نرمال‌سازی
/// ApplicationDbContext.NormalizeDateTime را روی ورودی‌های bind شده اعمال می‌کند تا
/// فیلترهای تاریخ در همهٔ صفحات بدون تغییر کنترلرها کار کنند.
///
/// وقتی تقویمِ «هجری شمسی افغانستان» فعال است، متنِ شمسی (مثل 1405/07/12 یا ۱۴۰۵/۰۷/۱۲) همین‌جا
/// اعتبارسنجی و به روزِ میلادیِ کانونیک تبدیل می‌شود؛ پس هیچ کنترلری منطقِ تقویم ندارد و مقدارِ ISO
/// میلادی (مقدارِ input تاریخ) مثل قبل از binder داخلی می‌گذرد.
/// </summary>
public sealed class UtcDateTimeModelBinder : IModelBinder
{
    private readonly IModelBinder _inner;

    public UtcDateTimeModelBinder(IModelBinder inner) => _inner = inner;

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        if (TryBindSolarHijri(bindingContext))
            return;

        await _inner.BindModelAsync(bindingContext);

        if (!bindingContext.Result.IsModelSet || bindingContext.Result.Model is not DateTime value)
            return;

        var normalized = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        if (normalized != value || normalized.Kind != value.Kind)
            bindingContext.Result = ModelBindingResult.Success(normalized);
    }

    private static bool TryBindSolarHijri(ModelBindingContext bindingContext)
    {
        if (!AppCalendarContext.IsSolarHijri)
            return false;

        var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        var raw = valueResult.FirstValue;
        if (valueResult == ValueProviderResult.None || !AfghanSolarCalendar.LooksLikeSolar(raw))
            return false;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
        if (AfghanSolarCalendar.TryParse(raw, out var solar, out var error))
        {
            bindingContext.Result = ModelBindingResult.Success(solar.ToGregorian());
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, error!);
            bindingContext.Result = ModelBindingResult.Failed();
        }

        return true;
    }
}

public sealed class UtcDateTimeModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.UnderlyingOrModelType;
        if (modelType != typeof(DateTime))
            return null;

        var loggerFactory = (ILoggerFactory)context.Services.GetService(typeof(ILoggerFactory))!;
        return new UtcDateTimeModelBinder(new SimpleTypeModelBinder(context.Metadata.ModelType, loggerFactory));
    }
}
