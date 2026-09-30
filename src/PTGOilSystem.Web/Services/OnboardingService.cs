using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// وضعیت چک‌لیست راه‌اندازی، فقط از روی دادهٔ واقعیِ ثبت‌شده. هیچ جدول یا فیلد تازه‌ای
/// لازم ندارد و چیزی نمی‌نویسد؛ هر مرحله وقتی تمام است که رکورد مربوطش واقعاً وجود دارد.
/// </summary>
public interface IOnboardingService
{
    Task<OnboardingProgress> GetProgressAsync(CancellationToken ct = default);
}

/// <summary>ترتیب همان وابستگی واقعی ثبت است: شرکت → محصول → طرف حساب → قرارداد → بارگیری.</summary>
public sealed record OnboardingProgress(
    bool HasCompany,
    bool HasOwnerCompany,
    bool HasProduct,
    bool HasParty,
    bool HasContract,
    bool HasLoading)
{
    public bool IsComplete => HasOwnerCompany && HasProduct && HasParty && HasContract && HasLoading;
}

public sealed class OnboardingService(ApplicationDbContext db) : IOnboardingService
{
    public async Task<OnboardingProgress> GetProgressAsync(CancellationToken ct = default)
    {
        // DbContext هم‌زمانی ندارد؛ پرس‌وجوها پشت‌سرهم و فقط Any هستند.
        var hasCompany = await db.Companies.AsNoTracking().AnyAsync(ct);
        var hasOwner = hasCompany && await db.Companies.AsNoTracking().AnyAsync(c => c.IsSystemOwner, ct);
        var hasProduct = await db.Products.AsNoTracking().AnyAsync(ct);
        var hasParty = await db.Suppliers.AsNoTracking().AnyAsync(ct)
            || await db.Customers.AsNoTracking().AnyAsync(ct);
        var hasContract = await db.Contracts.AsNoTracking().AnyAsync(ct);
        var hasLoading = await db.LoadingRegisters.AsNoTracking().AnyAsync(ct);

        return new OnboardingProgress(hasCompany, hasOwner, hasProduct, hasParty, hasContract, hasLoading);
    }
}
