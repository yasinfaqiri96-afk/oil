using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.Parties;

/// <summary>شناسهٔ یکتای یک طرف‌حساب: نوع + شناسه در جدول Master خودش.</summary>
public readonly record struct PartyKey(PartyStatementPartyType PartyType, int PartyId);

/// <summary>
/// تنها مالکِ «هویت طرف‌حساب» در سیستم: نام نمایشی، مشخصات کامل و مسیر صفحهٔ جزئیات.
///
/// پیش از این سه پیاده‌سازی موازی وجود داشت — یکی در PartyBalanceReadService، یکی در
/// PartyStatementReadService و یکی در OperationalAssetsController — که هر کدام هشت جدول
/// Master را جداگانه می‌خواندند. افزودن یک نوع طرف‌حساب یعنی سه ویرایش، و جا افتادن یکی
/// از آن‌ها یعنی ردیفی که نامش «-» می‌شود بی‌آنکه خطایی دیده شود.
///
/// دو قرارداد نام عمداً از هم جدا مانده‌اند و یکی نمی‌شوند:
///   <see cref="GetNamesAsync"/>   نامِ فهرست/برچسب — همان Name یا FullName خام.
///   <see cref="GetProfileAsync"/> پروندهٔ سربرگ صورت‌حساب — NamePersian در اولویت، به‌همراه کد و تماس.
/// </summary>
public interface IPartyDirectory
{
    Task<IReadOnlyDictionary<PartyKey, string>> GetNamesAsync(
        IReadOnlyCollection<PartyKey> keys,
        CancellationToken cancellationToken = default);

    Task<PartyStatementPartyInfo?> GetProfileAsync(
        PartyKey key,
        CancellationToken cancellationToken = default);

    /// <summary>نام کنترلرِ صفحهٔ جزئیات این نوع طرف‌حساب.</summary>
    string DetailsController(PartyStatementPartyType partyType);
}
