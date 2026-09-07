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

        // فیلتر روی خودِ موجودیت اعمال می‌شود و projection بعد از آن می‌آید. اگر روی یک
        // IQueryable از ValueTuple فیلتر شود، EF کل tuple را مقایسه می‌کند و query ترجمه
        // نمی‌شود؛ آن خطا فقط وقتی داده وجود داشت بروز می‌کرد.
        async Task AddAsync<TEntity>(
            PartyStatementPartyType partyType,
            IQueryable<TEntity> source,
            Func<IQueryable<TEntity>, int[], IQueryable<PartyNameRow>> project)
            where TEntity : class
        {
            var ids = keys
                .Where(key => key.PartyType == partyType)
                .Select(key => key.PartyId)
                .Distinct()
                .ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            var rows = await project(source.AsNoTracking(), ids).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                result[new PartyKey(partyType, row.Id)] = row.Name;
            }
        }

        await AddAsync(PartyStatementPartyType.Customer, db.Customers,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));
        await AddAsync(PartyStatementPartyType.Supplier, db.Suppliers,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));
        await AddAsync(PartyStatementPartyType.ServiceProvider, db.ServiceProviders,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));
        await AddAsync(PartyStatementPartyType.Sarraf, db.Sarrafs,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));
        await AddAsync(PartyStatementPartyType.Driver, db.Drivers,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.FullName)));
        await AddAsync(PartyStatementPartyType.Employee, db.Employees,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.FullName)));
        await AddAsync(PartyStatementPartyType.Partner, db.Partners,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));
        await AddAsync(PartyStatementPartyType.Company, db.Companies,
            (q, ids) => q.Where(x => ids.Contains(x.Id)).Select(x => new PartyNameRow(x.Id, x.Name)));

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

    private sealed record PartyNameRow(int Id, string Name);
}
