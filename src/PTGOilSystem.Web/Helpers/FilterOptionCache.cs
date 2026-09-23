using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// گزینه‌های کشویی فیلترِ فهرست‌ها از روی خودِ جدول عملیاتی ساخته می‌شوند (DISTINCT روی
/// بارگیری/رسید)، پس هر بار باز شدن صفحه یک اسکن کامل بود. این کش همان فهرست را برای یک
/// بازهٔ کوتاه نگه می‌دارد تا اسکن در هر رفرش تکرار نشود؛ محتوای فهرست و ترتیب آن عوض نمی‌شود.
/// اگر کش در دسترس نباشد (مثلاً در تست‌ها) دقیقاً مثل قبل هر بار از دیتابیس خوانده می‌شود.
/// </summary>
public static class FilterOptionCache
{
    /// <summary>عمر کش: کوتاه، تا گزینهٔ تازه حداکثر پس از یک دقیقه در فیلتر دیده شود.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public static async Task<List<SelectListItem>> GetOrCreateAsync(
        IMemoryCache? cache,
        string key,
        Func<Task<List<SelectListItem>>> factory)
    {
        if (cache is null)
        {
            return await factory();
        }

        if (cache.TryGetValue(key, out List<SelectListItem>? cached) && cached is not null)
        {
            return cached;
        }

        var options = await factory();
        cache.Set(key, options, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });
        return options;
    }
}
