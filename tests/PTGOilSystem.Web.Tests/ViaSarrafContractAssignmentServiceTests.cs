using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ViaSarrafContractAssignmentServiceTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Guid SeedGroup(
        ApplicationDbContext db,
        int supplierId = 1,
        int sarrafId = 5,
        int? contractId = null,
        decimal amount = 100000m,
        decimal amountUsd = 1250m,
        string currency = "RUB")
    {
        var groupId = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 7, 1),
                Side = LedgerSide.Debit,
                AmountUsd = amountUsd,
                SourceAmount = amount,
                SourceCurrencyCode = currency,
                SourceType = PaymentsController.ViaSarrafSupplierLedgerSourceType,
                SourceId = sarrafId,
                SupplierId = supplierId,
                ContractId = contractId,
                ViaSarrafGroupId = groupId,
                Description = "x"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 7, 1),
                Side = LedgerSide.Credit,
                AmountUsd = amountUsd,
                SourceAmount = amount,
                SourceCurrencyCode = currency,
                SourceType = PaymentsController.ViaSarrafPayableLedgerSourceType,
                SourceId = sarrafId,
                ContractId = contractId,
                ViaSarrafGroupId = groupId,
                Description = "y"
            });
        db.SaveChanges();
        return groupId;
    }

    private static void SeedContracts(ApplicationDbContext db)
    {
        db.Companies.AddRange(
            new Company { Id = 1, Name = "Co1", IsActive = true },
            new Company { Id = 2, Name = "Co2", IsActive = true });
        db.Suppliers.AddRange(
            new Supplier { Id = 1, Name = "Sup1" },
            new Supplier { Id = 2, Name = "Sup2" });
        db.Sarrafs.Add(new Sarraf { Id = 5, Name = "Sarraf1" });
        db.Contracts.AddRange(
            new Contract { Id = 10, ContractNumber = "P-10", ContractType = ContractType.Purchase, Status = ContractStatus.Active, CompanyId = 1, ProductId = 1, SupplierId = 1, ContractDate = new DateTime(2026, 1, 1), QuantityMt = 1 },
            new Contract { Id = 11, ContractNumber = "P-11", ContractType = ContractType.Purchase, Status = ContractStatus.Active, CompanyId = 1, ProductId = 1, SupplierId = 1, ContractDate = new DateTime(2026, 1, 1), QuantityMt = 1 },
            new Contract { Id = 12, ContractNumber = "P-12", ContractType = ContractType.Purchase, Status = ContractStatus.Active, CompanyId = 2, ProductId = 1, SupplierId = 1, ContractDate = new DateTime(2026, 1, 1), QuantityMt = 1 },
            new Contract { Id = 20, ContractNumber = "P-20", ContractType = ContractType.Purchase, Status = ContractStatus.Active, CompanyId = 1, ProductId = 1, SupplierId = 2, ContractDate = new DateTime(2026, 1, 1), QuantityMt = 1 },
            new Contract { Id = 30, ContractNumber = "S-30", ContractType = ContractType.Sale, Status = ContractStatus.Active, CompanyId = 1, ProductId = 1, SupplierId = 1, ContractDate = new DateTime(2026, 1, 1), QuantityMt = 1 });
        db.SaveChanges();
    }

    [Fact] // Test 1 + 2 + 11: assign contract to a group without one, same id on both rows.
    public async Task Assign_Sets_Same_ContractId_On_Both_Rows()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);

        var svc = new ViaSarrafContractAssignmentService(db);
        var result = await svc.AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 10, "forgot", 7, "tester"));

        Assert.Null(result.PreviousContractId);
        Assert.Equal(10, result.NewContractId);
        var rows = await db.LedgerEntries.Where(l => l.ViaSarrafGroupId == groupId).ToListAsync();
        Assert.All(rows, r => Assert.Equal(10, r.ContractId));
    }

    [Fact] // Test 10 + 15: assignment creates no new ledger rows.
    public async Task Assign_Does_Not_Create_New_Ledger_Rows()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);

        await new ViaSarrafContractAssignmentService(db)
            .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 10, "r", null, null));

        Assert.Equal(2, await db.LedgerEntries.CountAsync());
    }

    [Fact] // Test 3: change existing contract to a new one.
    public async Task Assign_Changes_Existing_Contract()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db, contractId: 10);

        var result = await new ViaSarrafContractAssignmentService(db)
            .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 11, "fix", null, null));

        Assert.Equal(10, result.PreviousContractId);
        Assert.Equal(11, result.NewContractId);
        Assert.All(await db.LedgerEntries.Where(l => l.ViaSarrafGroupId == groupId).ToListAsync(),
            r => Assert.Equal(11, r.ContractId));
    }

    [Fact] // Test 4: clearing the contract (remove).
    public async Task Assign_Null_Removes_Contract()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db, contractId: 10);

        await new ViaSarrafContractAssignmentService(db)
            .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, null, "remove", null, null));

        Assert.All(await db.LedgerEntries.Where(l => l.ViaSarrafGroupId == groupId).ToListAsync(),
            r => Assert.Null(r.ContractId));
    }

    [Fact] // Test 5: reject a contract of a different supplier.
    public async Task Assign_Rejects_Other_Supplier_Contract()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafContractAssignmentService(db)
                .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 20, "r", null, null)));
        Assert.Equal("VIA_SARRAF_CONTRACT_SUPPLIER_MISMATCH", ex.Code);
    }

    [Fact] // Test 7: reject a sale contract.
    public async Task Assign_Rejects_Sale_Contract()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafContractAssignmentService(db)
                .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 30, "r", null, null)));
        Assert.Equal("VIA_SARRAF_CONTRACT_NOT_PURCHASE", ex.Code);
    }

    [Fact] // Test 6 + 13: reject reassigning to a contract in a different company.
    public async Task Assign_Rejects_Different_Company_On_Reassign()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db, contractId: 10); // contract 10 is company 1

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafContractAssignmentService(db)
                .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 12, "r", null, null))); // 12 is company 2
        Assert.Equal("VIA_SARRAF_CONTRACT_COMPANY_MISMATCH", ex.Code);
    }

    [Fact] // Test 8 (malformed): a group with three rows is rejected, nothing changes.
    public async Task Assign_Rejects_Malformed_Group_With_Three_Rows()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 7, 1),
            Side = LedgerSide.Credit,
            SourceType = PaymentsController.ViaSarrafPayableLedgerSourceType,
            SourceId = 5,
            ViaSarrafGroupId = groupId,
            Description = "extra"
        });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafContractAssignmentService(db)
                .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 10, "r", null, null)));
        Assert.Equal("VIA_SARRAF_GROUP_MALFORMED", ex.Code);
    }

    [Fact] // reason is mandatory.
    public async Task Assign_Requires_Reason()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var groupId = SeedGroup(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new ViaSarrafContractAssignmentService(db)
                .AssignContractAsync(new ViaSarrafContractAssignmentRequest(groupId, 10, "  ", null, null)));
        Assert.Equal("VIA_SARRAF_CONTRACT_REASON_REQUIRED", ex.Code);
    }
}
