using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Parties;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// هویت طرف‌حساب یک مالک دارد. این تست‌ها هر هشت نوع را می‌پوشانند تا افزودن نوع تازه بدون
/// به‌روزرسانی دایرکتوری، همین‌جا شکست بخورد و نه در ردیفی که نامش خاموش «-» می‌شود.
/// </summary>
public sealed class PartyDirectoryTests
{
    private static async Task<ApplicationDbContext> BuildDbAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        db.Customers.Add(new Customer { Id = 1, Name = "Customer One", NamePersian = "مشتری یک", Code = "C-1", Phone = "070", Address = "Kabul" });
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier Two", NamePersian = "تأمین‌کننده دو", Code = "S-2" });
        db.ServiceProviders.Add(new ServiceProvider { Id = 3, Name = "Carrier Three", Code = "SP-3", Email = "a@b.c" });
        db.Sarrafs.Add(new Sarraf { Id = 4, Name = "Sarraf Four", PhoneNumber = "0700" });
        db.Drivers.Add(new Driver { Id = 5, FullName = "Driver Five", LicenseNumber = "L-5" });
        db.Employees.Add(new Employee { Id = 6, FullName = "Employee Six", EmployeeCode = "E-6" });
        db.Partners.Add(new Partner { Id = 7, Name = "Partner Seven", NamePersian = "شریک هفت", Code = "P-7" });
        db.Companies.Add(new Company { Id = 8, Name = "Company Eight", NamePersian = "شرکت هشت", Code = "CO-8", Country = "AF" });
        await db.SaveChangesAsync();
        return db;
    }

    public static TheoryData<PartyStatementPartyType, int, string> ExpectedNames => new()
    {
        { PartyStatementPartyType.Customer, 1, "Customer One" },
        { PartyStatementPartyType.Supplier, 2, "Supplier Two" },
        { PartyStatementPartyType.ServiceProvider, 3, "Carrier Three" },
        { PartyStatementPartyType.Sarraf, 4, "Sarraf Four" },
        { PartyStatementPartyType.Driver, 5, "Driver Five" },
        { PartyStatementPartyType.Employee, 6, "Employee Six" },
        { PartyStatementPartyType.Partner, 7, "Partner Seven" },
        { PartyStatementPartyType.Company, 8, "Company Eight" }
    };

    [Theory]
    [MemberData(nameof(ExpectedNames))]
    public async Task Resolves_The_List_Name_Of_Every_Party_Type(
        PartyStatementPartyType partyType,
        int partyId,
        string expected)
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);

        var names = await directory.GetNamesAsync([new PartyKey(partyType, partyId)]);

        Assert.Equal(expected, names[new PartyKey(partyType, partyId)]);
    }

    [Fact]
    public async Task Resolves_Every_Party_Type_In_One_Round()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);
        var keys = Enum.GetValues<PartyStatementPartyType>()
            .Select(type => new PartyKey(type, ExpectedIdOf(type)))
            .ToArray();

        var names = await directory.GetNamesAsync(keys);

        Assert.Equal(keys.Length, names.Count);
        Assert.All(names.Values, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    /// <summary>
    /// سربرگ صورت‌حساب نامِ فارسی را ترجیح می‌دهد؛ فهرست‌ها نامِ خام را. این دو قرارداد
    /// عمداً جدا مانده‌اند و یکی‌شدنشان نامِ نمایش‌داده‌شده را در صفحات موجود عوض می‌کند.
    /// </summary>
    [Fact]
    public async Task Profile_Prefers_The_Persian_Name_While_List_Keeps_The_Raw_Name()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);
        var key = new PartyKey(PartyStatementPartyType.Customer, 1);

        var profile = await directory.GetProfileAsync(key);
        var names = await directory.GetNamesAsync([key]);

        Assert.NotNull(profile);
        Assert.Equal("مشتری یک", profile!.Name);
        Assert.Equal("C-1", profile.Code);
        Assert.Equal("Customer One", names[key]);
    }

    [Fact]
    public async Task Profile_Is_Available_For_Every_Party_Type()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);

        foreach (var type in Enum.GetValues<PartyStatementPartyType>())
        {
            var profile = await directory.GetProfileAsync(new PartyKey(type, ExpectedIdOf(type)));
            Assert.NotNull(profile);
            Assert.False(string.IsNullOrWhiteSpace(profile!.Name));
        }
    }

    [Fact]
    public async Task Missing_Party_Returns_Null_Profile_And_No_Name_Row()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);
        var key = new PartyKey(PartyStatementPartyType.Customer, 404);

        Assert.Null(await directory.GetProfileAsync(key));
        Assert.Empty(await directory.GetNamesAsync([key]));
    }

    [Fact]
    public async Task Empty_Request_Does_Not_Query()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);

        Assert.Empty(await directory.GetNamesAsync([]));
    }

    [Fact]
    public async Task Every_Party_Type_Has_A_Details_Controller()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);

        foreach (var type in Enum.GetValues<PartyStatementPartyType>())
        {
            Assert.False(string.IsNullOrWhiteSpace(directory.DetailsController(type)));
        }
    }

    [Fact]
    public async Task Rejects_An_Unknown_Party_Type_Instead_Of_Guessing()
    {
        await using var db = await BuildDbAsync();
        var directory = new PartyDirectory(db);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => directory.DetailsController((PartyStatementPartyType)99));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => directory.GetProfileAsync(new PartyKey((PartyStatementPartyType)99, 1)));
    }

    private static int ExpectedIdOf(PartyStatementPartyType type) => type switch
    {
        PartyStatementPartyType.Customer => 1,
        PartyStatementPartyType.Supplier => 2,
        PartyStatementPartyType.ServiceProvider => 3,
        PartyStatementPartyType.Sarraf => 4,
        PartyStatementPartyType.Driver => 5,
        PartyStatementPartyType.Employee => 6,
        PartyStatementPartyType.Partner => 7,
        PartyStatementPartyType.Company => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
