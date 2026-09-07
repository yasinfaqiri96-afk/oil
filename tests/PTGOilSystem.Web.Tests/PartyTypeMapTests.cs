using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Parties;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// دو شمارشِ طرف‌حساب مقادیر عددیِ ناهماهنگ دارند، پس یک cast ساده بدهیِ راننده را روی
/// حساب کارمند یا شریک می‌نشاند. این تست‌ها همان ناهماهنگی را صریح قفل می‌کنند تا اگر روزی
/// کسی مقادیر را «مرتب» کرد یا یک نوع تازه اضافه کرد، همین‌جا شکست بخورد نه در صورت‌حساب.
/// </summary>
public sealed class PartyTypeMapTests
{
    public static TheoryData<AccountingPartyType, PartyStatementPartyType> Pairs => new()
    {
        { AccountingPartyType.Customer, PartyStatementPartyType.Customer },
        { AccountingPartyType.Supplier, PartyStatementPartyType.Supplier },
        { AccountingPartyType.ServiceProvider, PartyStatementPartyType.ServiceProvider },
        { AccountingPartyType.Sarraf, PartyStatementPartyType.Sarraf },
        { AccountingPartyType.Driver, PartyStatementPartyType.Driver },
        { AccountingPartyType.Employee, PartyStatementPartyType.Employee },
        { AccountingPartyType.Partner, PartyStatementPartyType.Partner },
        { AccountingPartyType.Company, PartyStatementPartyType.Company }
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Maps_Each_Pair_Both_Ways(AccountingPartyType accounting, PartyStatementPartyType statement)
    {
        Assert.Equal(statement, PartyTypeMap.ToStatement(accounting));
        Assert.Equal(accounting, PartyTypeMap.ToAccounting(statement));
    }

    [Fact]
    public void Covers_Every_Accounting_Value()
    {
        foreach (var value in Enum.GetValues<AccountingPartyType>())
        {
            var statement = PartyTypeMap.ToStatement(value);
            Assert.Equal(value, PartyTypeMap.ToAccounting(statement));
        }
    }

    [Fact]
    public void Covers_Every_Statement_Value()
    {
        foreach (var value in Enum.GetValues<PartyStatementPartyType>())
        {
            var accounting = PartyTypeMap.ToAccounting(value);
            Assert.Equal(value, PartyTypeMap.ToStatement(accounting));
        }
    }

    /// <summary>
    /// اگر این تست روزی بشکند یعنی مقادیر عددی هم‌تراز شده‌اند. آن‌وقت هم نگاشت باید
    /// از همین‌جا برود، ولی این تست باید آگاهانه حذف شود، نه تصادفی.
    /// </summary>
    [Theory]
    [InlineData(AccountingPartyType.Driver, PartyStatementPartyType.Driver)]
    [InlineData(AccountingPartyType.Employee, PartyStatementPartyType.Employee)]
    [InlineData(AccountingPartyType.Partner, PartyStatementPartyType.Partner)]
    public void Numeric_Cast_Would_Have_Been_Wrong(
        AccountingPartyType accounting,
        PartyStatementPartyType statement)
    {
        Assert.NotEqual((int)accounting, (int)statement);
        Assert.Equal(statement, PartyTypeMap.ToStatement(accounting));
    }

    [Fact]
    public void Rejects_Unknown_Values_Instead_Of_Guessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyTypeMap.ToStatement((AccountingPartyType)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyTypeMap.ToAccounting((PartyStatementPartyType)99));
    }

    [Fact]
    public void Null_Stays_Null()
    {
        Assert.Null(PartyTypeMap.ToStatementOrNull(null));
        Assert.Null(PartyTypeMap.ToAccountingOrNull(null));
        Assert.Equal(
            PartyStatementPartyType.Driver,
            PartyTypeMap.ToStatementOrNull(AccountingPartyType.Driver));
    }
}
