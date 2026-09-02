using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک حملِ فیزیکی واحد می‌تواند بارِ چند شرکت را با هم ببرد. این تست‌ها می‌گویند مالکیتِ
/// اقتصادیِ هر سهم از کجا خوانده می‌شود: از سهم‌های منبع، نه از قرارداد سرصفحهٔ حمل.
/// </summary>
public class InventoryTransportLegOwnershipResolverTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    // P-016 و P-018 مال شرکت A، P-017 مال شرکت B.
    private const int ContractP016 = 16;
    private const int ContractP017 = 17;
    private const int ContractP018 = 18;

    [Fact]
    public async Task A_Leg_Without_Allocations_Falls_Back_To_Its_Header_Contract()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 30m, headerContractId: ContractP016);

        var slices = await Resolve(db, leg);

        var slice = Assert.Single(slices);
        Assert.Equal(CompanyA, slice.CompanyId);
        Assert.Equal(30m, slice.QuantityMt);
        Assert.Equal(ContractP016, slice.SingleContractId);
    }

    [Fact]
    public async Task A_Single_Company_Leg_Stays_A_Single_Slice()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 30m, headerContractId: ContractP016);
        await AddAllocationsAsync(db, leg.Id, (ContractP016, 30m));

        var slices = await Resolve(db, leg);

        var slice = Assert.Single(slices);
        Assert.Equal(CompanyA, slice.CompanyId);
        Assert.Equal(30m, slice.QuantityMt);
    }

    // موتر ۳۰ تنی: ۱۰ تن از P-016/شرکت A و ۲۰ تن از P-017/شرکت B.
    [Fact]
    public async Task A_Multi_Company_Leg_Splits_By_The_Owner_Of_Each_Source()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 30m, headerContractId: ContractP016);
        await AddAllocationsAsync(db, leg.Id, (ContractP016, 10m), (ContractP017, 20m));

        var slices = await Resolve(db, leg);

        Assert.Equal(2, slices.Count);
        Assert.Equal(10m, slices.Single(x => x.CompanyId == CompanyA).QuantityMt);
        Assert.Equal(20m, slices.Single(x => x.CompanyId == CompanyB).QuantityMt);
        Assert.Equal(leg.QuantityMt, slices.Sum(x => x.QuantityMt));
    }

    // دو قرارداد از یک شرکت پیش از حسابداری با هم جمع می‌شوند: A = 10 + 5.
    [Fact]
    public async Task Two_Contracts_Of_The_Same_Company_Are_Grouped_Into_One_Slice()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 30m, headerContractId: ContractP016);
        await AddAllocationsAsync(db, leg.Id, (ContractP016, 10m), (ContractP018, 5m), (ContractP017, 15m));

        var slices = await Resolve(db, leg);

        Assert.Equal(2, slices.Count);
        var companyA = slices.Single(x => x.CompanyId == CompanyA);
        Assert.Equal(15m, companyA.QuantityMt);
        Assert.Equal([ContractP016, ContractP018], companyA.ContractIds);
        // چند قرارداد در یک شرکت یعنی بُعدِ قرارداد روی سند معنای واحد ندارد.
        Assert.Null(companyA.SingleContractId);
        Assert.Equal(15m, slices.Single(x => x.CompanyId == CompanyB).QuantityMt);
        Assert.Equal(leg.QuantityMt, slices.Sum(x => x.QuantityMt));
    }

    // مقدارِ حمل مبناست، نه جمعِ سهم‌ها: سهم‌ها مقیاس می‌شوند و هیچ کسری بی‌مالک نمی‌ماند.
    [Fact]
    public async Task Slices_Always_Add_Up_To_The_Leg_Quantity()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 10m, headerContractId: ContractP016);
        await AddAllocationsAsync(db, leg.Id, (ContractP016, 1m), (ContractP017, 2m));

        var slices = await Resolve(db, leg);

        Assert.Equal(10m, slices.Sum(x => x.QuantityMt));
        Assert.Equal(3.3333m, slices.Single(x => x.CompanyId == CompanyA).QuantityMt);
        Assert.Equal(6.6667m, slices.Single(x => x.CompanyId == CompanyB).QuantityMt);
    }

    [Fact]
    public async Task An_Unprovable_Owner_Yields_No_Slices()
    {
        await using var db = BuildDb();
        var leg = await SeedLegAsync(db, quantityMt: 30m, headerContractId: 999);

        var slices = await Resolve(db, leg);

        Assert.Empty(slices);
    }

    private static Task<IReadOnlyList<LegCompanyOwnershipSlice>> Resolve(
        ApplicationDbContext db,
        InventoryTransportLeg leg)
        => new InventoryTransportLegOwnershipResolver(db).ResolveCompanyOwnershipSlicesAsync(leg);

    private static ApplicationDbContext BuildDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Companies.AddRange(
            new Company { Id = CompanyA, Code = "A", Name = "Company A", IsActive = true },
            new Company { Id = CompanyB, Code = "B", Name = "Company B", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Contracts.AddRange(
            NewContract(ContractP016, CompanyA, "P-016"),
            NewContract(ContractP017, CompanyB, "P-017"),
            NewContract(ContractP018, CompanyA, "P-018"));
        db.SaveChanges();
        return db;
    }

    private static Contract NewContract(int id, int companyId, string number)
        => new()
        {
            Id = id,
            CompanyId = companyId,
            ContractNumber = number,
            ContractType = ContractType.Purchase,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 1000m
        };

    private static async Task<InventoryTransportLeg> SeedLegAsync(
        ApplicationDbContext db,
        decimal quantityMt,
        int headerContractId)
    {
        var leg = new InventoryTransportLeg
        {
            Id = 1,
            SourcePurchaseContractId = headerContractId,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Truck,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = quantityMt,
            Status = InventoryTransportLegStatus.Loaded
        };
        db.InventoryTransportLegs.Add(leg);
        await db.SaveChangesAsync();
        return leg;
    }

    private static async Task AddAllocationsAsync(
        ApplicationDbContext db,
        int legId,
        params (int ContractId, decimal QuantityMt)[] allocations)
    {
        db.InventoryTransportLegAllocations.AddRange(allocations.Select(a => new InventoryTransportLegAllocation
        {
            InventoryTransportLegId = legId,
            SourcePurchaseContractId = a.ContractId,
            QuantityMt = a.QuantityMt
        }));
        await db.SaveChangesAsync();
    }
}
