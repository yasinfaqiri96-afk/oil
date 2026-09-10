namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// تنها قاعدهٔ تقسیم سود بین شرکا — و تنها جایی که «سنتِ باقیمانده» تعیین تکلیف می‌شود.
///
/// چرا جدا: صورت‌حساب شراکت سهم را برای نمایش می‌سازد و دفتر کل همان سهم را ثبت می‌کند.
/// اگر هرکدام جداگانه گِرد کنند، دو عددِ متفاوت به‌دست می‌آید و مغایرتِ یک سِنتی می‌ماند که
/// هیچ‌کس نمی‌تواند اثباتش کند. هر دو از اینجا می‌خوانند، پس اختلاف اصلاً به‌وجود نمی‌آید.
///
/// قاعده:
///   ۱) هر سهم به دو رقم اعشار گِرد می‌شود (AwayFromZero — همان قاعدهٔ پولی بقیهٔ سیستم).
///   ۲) باقیماندهٔ جمع نسبت به کلِ سود، یک‌جا به یک شریکِ معیّن می‌رود:
///      بزرگ‌ترین سهمِ خام، و در تساوی، کوچک‌ترین PartnerId.
///
/// نتیجه: <c>Sum(shares) == total</c> همیشه دقیق است و انتخابِ شریکِ گیرندهٔ سِنت، به ترتیبِ
/// دیکشنری یا ترتیبِ خواندن از دیتابیس وابسته نیست.
/// </summary>
public static class PartnerProfitAllocationPolicy
{
    public static decimal Round(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// سهم‌های خام را به سهم‌های پولی تبدیل می‌کند، بدون اینکه گِردکردن چیزی به جمع اضافه یا از
    /// آن کم کند.
    ///
    /// مرجعِ جمع، خودِ سهم‌های خام است نه کلِ سود — چون همیشه همهٔ سود سهم‌دار ندارد. قراردادی
    /// که فقط یک شریکِ ۲۵٪ در آن ثبت شده، عمداً یک‌چهارمِ سود را نشان می‌دهد؛ اگر باقیمانده را
    /// نسبت به کلِ سود حساب می‌کردیم، سه‌چهارمِ بی‌صاحب هم به همان یک شریک چسبانده می‌شد.
    /// وقتی سهم‌ها کلِ صددرصد را می‌پوشانند — که حالتِ عادی است — جمعِ خام برابر کلِ سود است و
    /// نتیجه همان «جمعِ سهم‌ها دقیقاً برابر سود» می‌شود.
    /// </summary>
    /// <param name="rawByPartner">سهمِ گِردنشدهٔ هر شریک.</param>
    public static Dictionary<int, decimal> Settle(IReadOnlyDictionary<int, decimal> rawByPartner)
    {
        ArgumentNullException.ThrowIfNull(rawByPartner);

        var settled = rawByPartner.ToDictionary(x => x.Key, x => Round(x.Value));
        if (settled.Count == 0)
            return settled;

        var residual = Round(rawByPartner.Values.Sum()) - settled.Values.Sum();
        if (residual == 0m)
            return settled;

        var carrier = rawByPartner
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .First()
            .Key;
        settled[carrier] = Round(settled[carrier] + residual);
        return settled;
    }
}
