using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.Parties;

/// <summary>
/// پیادهٔ <see cref="IPartyDirectory"/> روی جدول‌های Master. ببینید توضیح رابط برای اینکه
/// چرا این تنها مالک هویت طرف‌حساب است.
/// </summary>
public sealed class PartyDirectory(ApplicationDbContext db) : IPartyDirectory
{
    public async Task<IReadOnlyDictionary<PartyKey, string>> GetNamesAsync(
        IReadOnlyCollection<PartyKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var result = new Dictionary<PartyKey, string>();
        if (keys.Count == 0)
        {
            return result;
        }

        int[] IdsOf(PartyStatementPartyType partyType) => keys
            .Where(key => key.PartyType == partyType)
            .Select(key => key.PartyId)
            .Distinct()
            .ToArray();

        var customerIds = IdsOf(PartyStatementPartyType.Customer);
        var supplierIds = IdsOf(PartyStatementPartyType.Supplier);
        var serviceProviderIds = IdsOf(PartyStatementPartyType.ServiceProvider);
        var sarrafIds = IdsOf(PartyStatementPartyType.Sarraf);
        var driverIds = IdsOf(PartyStatementPartyType.Driver);
        var employeeIds = IdsOf(PartyStatementPartyType.Employee);
        var partnerIds = IdsOf(PartyStatementPartyType.Partner);
        var companyIds = IdsOf(PartyStatementPartyType.Company);

        // یک رفت‌وبرگشت به‌جای هشت‌تا. پیش از این برای هر نوع طرف‌حساب یک SELECT جدا
        // فرستاده می‌شد و صفحه‌هایی مثل «طلبات و بدهی‌ها» هر بار هشت بار به دیتابیس
        // می‌رفتند فقط برای نام. شاخه‌ها با UNION ALL یکی می‌شوند و هر شاخه هنوز همان
        // جست‌وجوی کلید اصلیِ خودش است (id = ANY(...))، پس نه index از کار می‌افتد و نه
        // نامی عوض می‌شود: دقیقاً همان ستون‌هایی خوانده می‌شود که قبلاً خوانده می‌شد.
        // شاخه‌ای که فهرست شناسه‌اش خالی است در PostgreSQL بلافاصله بی‌ردیف برمی‌گردد
        // (id = ANY('{}')) و جدولش خوانده نمی‌شود، پس همیشه هر هشت شاخه نوشته می‌شود.
        //
        // فیلتر روی خودِ موجودیت اعمال می‌شود و projection بعد از آن می‌آید. اگر روی یک
        // IQueryable از ValueTuple فیلتر شود، EF کل tuple را مقایسه می‌کند و query ترجمه
        // نمی‌شود؛ آن خطا فقط وقتی داده وجود داشت بروز می‌کرد.
        var merged = db.Customers.AsNoTracking()
                .Where(x => customerIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Customer, x.Id, Name = x.Name })
            .Concat(db.Suppliers.AsNoTracking()
                .Where(x => supplierIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Supplier, x.Id, Name = x.Name }))
            .Concat(db.ServiceProviders.AsNoTracking()
                .Where(x => serviceProviderIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.ServiceProvider, x.Id, Name = x.Name }))
            .Concat(db.Sarrafs.AsNoTracking()
                .Where(x => sarrafIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Sarraf, x.Id, Name = x.Name }))
            .Concat(db.Drivers.AsNoTracking()
                .Where(x => driverIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Driver, x.Id, Name = x.FullName }))
            .Concat(db.Employees.AsNoTracking()
                .Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Employee, x.Id, Name = x.FullName }))
            .Concat(db.Partners.AsNoTracking()
                .Where(x => partnerIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Partner, x.Id, Name = x.Name }))
            .Concat(db.Companies.AsNoTracking()
                .Where(x => companyIds.Contains(x.Id))
                .Select(x => new { PartyType = (int)PartyStatementPartyType.Company, x.Id, Name = x.Name }));

        foreach (var row in await merged.ToListAsync(cancellationToken))
        {
            result[new PartyKey((PartyStatementPartyType)row.PartyType, row.Id)] = row.Name;
        }

        return result;
    }

    public async Task<PartyStatementPartyInfo?> GetProfileAsync(
        PartyKey key,
        CancellationToken cancellationToken = default)
        => key.PartyType switch
        {
            PartyStatementPartyType.Customer => await db.Customers.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Supplier => await db.Suppliers.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.ServiceProvider => await db.ServiceProviders.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.Name, Code = x.Code, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Sarraf => await db.Sarrafs.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.Name, Code = null, Phone = x.PhoneNumber, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Employee => await db.Employees.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.FullName, Code = x.EmployeeCode, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Partner => await db.Partners.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Driver => await db.Drivers.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.FullName, Code = x.LicenseNumber, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            PartyStatementPartyType.Company => await db.Companies.AsNoTracking()
                .Where(x => x.Id == key.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Address = x.Address })
                .FirstOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key.PartyType,
                "Unknown statement party type; add it to PartyDirectory.")
        };

    public string DetailsController(PartyStatementPartyType partyType) => partyType switch
    {
        PartyStatementPartyType.Customer => "Customers",
        PartyStatementPartyType.Supplier => "Suppliers",
        PartyStatementPartyType.ServiceProvider => "ServiceProviders",
        PartyStatementPartyType.Sarraf => "Sarrafs",
        PartyStatementPartyType.Driver => "Drivers",
        PartyStatementPartyType.Employee => "Employees",
        PartyStatementPartyType.Partner => "Partners",
        PartyStatementPartyType.Company => "Companies",
        _ => throw new ArgumentOutOfRangeException(
            nameof(partyType),
            partyType,
            "Unknown statement party type; add it to PartyDirectory.")
    };

}
