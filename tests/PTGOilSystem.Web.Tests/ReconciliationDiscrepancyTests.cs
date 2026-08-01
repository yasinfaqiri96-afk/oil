using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Reconciliation;
using PTGOilSystem.Web.Services.Time;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// دسته‌های مغایرت تفصیلی. هر تست فقط داده‌ای می‌سازد که همان دسته را روشن کند و
/// اطمینان می‌دهد ردیف سالم وارد گزارش نمی‌شود.
/// </summary>
public class ReconciliationDiscrepancyTests
{
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ReconciliationService NewService(ApplicationDbContext db, DateTimeOffset? utcNow = null)
        => new(db, null, new AfghanistanBusinessClock(
            new FixedClock(utcNow ?? new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero))));

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static void SeedReference(ApplicationDbContext db)
    {
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
    }

    private static SalesTransaction NewSale(int id, decimal totalUsd = 5_000m, decimal qty = 10m) => new()
    {
        Id = id,
        CompanyId = 1,
        CustomerId = 1,
        ProductId = 1,
        InvoiceNumber = "INV-" + id,
        SaleDate = new DateTime(2026, 5, 1),
        QuantityMt = qty,
        UnitPriceUsd = totalUsd / qty,
        TotalUsd = totalUsd
    };

    [Fact]
    public async Task Sale_Without_Active_Cost_Consumption_Is_Reported_And_Costed_Sale_Is_Not()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(NewSale(1), NewSale(2), NewSale(3));
        // فروش ۲ بهای تمام‌شدهٔ فعال دارد؛ فروش ۳ فقط یک مصرف برگشت‌خورده دارد.
        db.SalesCostConsumptions.AddRange(
            new SalesCostConsumption
            {
                Id = 1,
                SalesTransactionId = 2,
                CompanyId = 1,
                ProductId = 1,
                QuantityMt = 10m,
                CostUsd = 4_000m,
                Status = SalesCostConsumptionStatus.Active
            },
            new SalesCostConsumption
            {
                Id = 2,
                SalesTransactionId = 3,
                CompanyId = 1,
                ProductId = 1,
                QuantityMt = 10m,
                CostUsd = 4_000m,
                Status = SalesCostConsumptionStatus.Reversed
            });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs);

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Rows, r => r.Reference == "INV-1");
        Assert.Contains(page.Rows, r => r.Reference == "INV-3");
        Assert.DoesNotContain(page.Rows, r => r.Reference == "INV-2");
    }

    [Fact]
    public async Task Cancelled_Sale_Is_Never_Reported_As_Missing_Cogs()
    {
        await using var db = NewDb();
        SeedReference(db);
        var cancelled = NewSale(1);
        cancelled.IsCancelled = true;
        db.SalesTransactions.Add(cancelled);
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs);

        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Sale_Without_Contract_And_Without_Inventory_Movement_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(NewSale(1), NewSale(2));
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            TerminalId = 1,
            ProductId = 1,
            SalesTransactionId = 2,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = 10m
        });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutContractOrInventorySource);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("INV-1", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Unbalanced_Journal_Entry_Is_Reported_With_Its_Difference()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.JournalEntries.AddRange(
            new JournalEntry
            {
                Id = 1,
                CompanyId = 1,
                JournalNumber = "JV-BAD",
                AccountingDate = new DateTime(2026, 5, 2),
                SourceModule = "Sales",
                SourceEntityType = "Sale",
                SourceEntityId = 1,
                Lines =
                {
                    new JournalEntryLine { Id = 1, AccountId = 1, Debit = 100m, Credit = 0m },
                    new JournalEntryLine { Id = 2, AccountId = 2, Debit = 0m, Credit = 40m }
                }
            },
            new JournalEntry
            {
                Id = 2,
                CompanyId = 1,
                JournalNumber = "JV-OK",
                AccountingDate = new DateTime(2026, 5, 3),
                SourceModule = "Sales",
                SourceEntityType = "Sale",
                SourceEntityId = 2,
                Lines =
                {
                    new JournalEntryLine { Id = 3, AccountId = 1, Debit = 100m, Credit = 0m },
                    new JournalEntryLine { Id = 4, AccountId = 2, Debit = 0m, Credit = 100m }
                }
            });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.UnbalancedJournal);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("JV-BAD", page.Rows[0].Reference);
        Assert.Equal(60m, page.Rows[0].AmountUsd);
    }

    [Fact]
    public async Task Journal_Without_Operational_Source_Is_Reported_But_Closing_Entries_Are_Not()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.JournalEntries.AddRange(
            new JournalEntry
            {
                Id = 1,
                CompanyId = 1,
                JournalNumber = "JV-ORPHAN",
                AccountingDate = new DateTime(2026, 5, 2),
                SourceModule = "Manual"
            },
            new JournalEntry
            {
                Id = 2,
                CompanyId = 1,
                JournalNumber = "JV-CLOSE",
                AccountingDate = new DateTime(2026, 5, 2),
                SourceModule = "Closing",
                IsClosing = true
            });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.JournalWithoutOperationalSource);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("JV-ORPHAN", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Ledger_Without_Matching_Journal_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 5, 1),
                Side = LedgerSide.Debit,
                AmountUsd = 500m,
                SourceType = "Sale",
                SourceId = 1,
                Reference = "LDG-1"
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 5, 1),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                SourceType = "Sale",
                SourceId = 2,
                Reference = "LDG-2"
            });
        db.JournalEntries.Add(new JournalEntry
        {
            Id = 1,
            CompanyId = 1,
            JournalNumber = "JV-1",
            AccountingDate = new DateTime(2026, 5, 1),
            SourceModule = "Sales",
            // دفتر حسابداری نام موجودیت را می‌نویسد، نه نام کوتاه دفتر عملیاتی ("Sale").
            SourceEntityType = nameof(SalesTransaction),
            SourceEntityId = 2
        });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.LedgerWithoutJournal);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("LDG-1", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Duplicate_Expense_Is_Flagged_But_Cancelled_Duplicate_Is_Not()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "FRT", Name = "Freight" });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction { Id = 1, ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 5, 1), AmountUsd = 250m },
            new ExpenseTransaction { Id = 2, ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 5, 1), AmountUsd = 250m },
            new ExpenseTransaction { Id = 3, ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 5, 4), AmountUsd = 999m },
            new ExpenseTransaction { Id = 4, ExpenseTypeId = 1, ExpenseDate = new DateTime(2026, 5, 4), AmountUsd = 999m, IsCancelled = true });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.PossiblyDoubleCountedExpense);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Rows, r => Assert.Contains("EXP-", r.Reference));
        Assert.DoesNotContain(page.Rows, r => r.Reference == "EXP-3");
    }

    [Fact]
    public async Task Duplicate_Customs_Reference_Is_Flagged()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.CustomsDeclarations.AddRange(
            new CustomsDeclaration { Id = 1, DeclarationReference = "CD-1", DeclarationDate = new DateTime(2026, 5, 1), TotalUsd = 100m },
            new CustomsDeclaration { Id = 2, DeclarationReference = "CD-1", DeclarationDate = new DateTime(2026, 5, 2), TotalUsd = 100m },
            new CustomsDeclaration { Id = 3, DeclarationReference = "CD-2", DeclarationDate = new DateTime(2026, 5, 3), TotalUsd = 100m });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.PossiblyDoubleCountedCustoms);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Rows, r => Assert.Equal("CD-1", r.Reference));
    }

    [Fact]
    public async Task Negative_Stock_Uses_The_Same_Sign_Convention_As_StockService()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.InventoryMovements.AddRange(
            new InventoryMovement { Id = 1, TerminalId = 1, ProductId = 1, Direction = MovementDirection.In, MovementDate = new DateTime(2026, 5, 1), QuantityMt = 10m },
            new InventoryMovement { Id = 2, TerminalId = 1, ProductId = 1, Direction = MovementDirection.Out, MovementDate = new DateTime(2026, 5, 2), QuantityMt = 12m });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.NegativeStock);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(-2m, page.Rows[0].QuantityMt);
    }

    [Fact]
    public async Task Over_Delivery_Beyond_PreSale_Commitment_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.PreSaleOrders.Add(new PreSaleOrder
        {
            Id = 1,
            OrderNumber = "PS-1",
            CustomerId = 1,
            ProductId = 1,
            CompanyId = 1,
            OrderDate = new DateTime(2026, 4, 1),
            QuantityMt = 10m,
            Status = PreSaleOrderStatus.PartiallyDelivered
        });
        var sale = NewSale(1, 6_000m, 12m);
        sale.PreSaleOrderId = 1;
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.OverDelivery);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("PS-1", page.Rows[0].Reference);
        Assert.Equal(2m, page.Rows[0].QuantityMt);
    }

    [Fact]
    public async Task Overdue_PreSale_Commitment_Uses_Kabul_Business_Date()
    {
        await using var db = NewDb();
        SeedReference(db);
        // مهلت تحویل ۳۰ ژوئیه. در ساعت ۱۹:۳۰ UTC، تاریخ کاری کابل ۳۱ ژوئیه است، پس سررسید گذشته است.
        db.PreSaleOrders.Add(new PreSaleOrder
        {
            Id = 1,
            OrderNumber = "PS-DUE",
            CustomerId = 1,
            ProductId = 1,
            CompanyId = 1,
            OrderDate = new DateTime(2026, 7, 1),
            ExpectedDeliveryTo = new DateTime(2026, 7, 30),
            QuantityMt = 10m,
            Status = PreSaleOrderStatus.Confirmed
        });
        await db.SaveChangesAsync();

        var beforeKabulMidnight = await NewService(db, new DateTimeOffset(2026, 7, 30, 19, 29, 59, TimeSpan.Zero))
            .BuildDiscrepancyPageAsync(ReconciliationDiscrepancyCategory.OverduePreSaleCommitment);
        Assert.Equal(0, beforeKabulMidnight.TotalCount);

        var afterKabulMidnight = await NewService(db, new DateTimeOffset(2026, 7, 30, 19, 30, 0, TimeSpan.Zero))
            .BuildDiscrepancyPageAsync(ReconciliationDiscrepancyCategory.OverduePreSaleCommitment);
        Assert.Equal(1, afterKabulMidnight.TotalCount);
        Assert.Equal("PS-DUE", afterKabulMidnight.Rows[0].Reference);
    }

    [Fact]
    public async Task Delivery_Booked_As_PreSale_Without_An_Order_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        var orphan = NewSale(1);
        orphan.SaleStage = SaleStage.PreSale;
        var linked = NewSale(2);
        linked.SaleStage = SaleStage.PreSale;
        linked.PreSaleOrderId = 1;
        db.PreSaleOrders.Add(new PreSaleOrder
        {
            Id = 1,
            OrderNumber = "PS-1",
            CustomerId = 1,
            ProductId = 1,
            CompanyId = 1,
            OrderDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            Status = PreSaleOrderStatus.Confirmed
        });
        db.SalesTransactions.AddRange(orphan, linked);
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.DeliveryWithoutPreSale);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("INV-1", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Reservation_Beyond_Available_Stock_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.PreSaleOrders.Add(new PreSaleOrder
        {
            Id = 1,
            OrderNumber = "PS-BIG",
            CustomerId = 1,
            ProductId = 1,
            CompanyId = 1,
            OrderDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            Status = PreSaleOrderStatus.Confirmed
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            TerminalId = 1,
            ProductId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1),
            QuantityMt = 20m
        });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.ReservationExceedsStock);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("PS-BIG", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Unallocated_Payment_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Cash" });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 5, 1),
                CashAccountId = 1,
                CompanyId = 1,
                CustomerId = 1,
                Amount = 900m,
                AmountUsd = 900m,
                Reference = "PAY-ORPHAN"
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 5, 2),
                CashAccountId = 1,
                CompanyId = 1,
                ContractId = 1,
                Amount = 500m,
                AmountUsd = 500m,
                Reference = "PAY-LINKED"
            });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.UnallocatedPayment);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("PAY-ORPHAN", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Draft_Or_Unposted_Sarraf_Settlement_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A" });
        db.SarrafSettlements.AddRange(
            new SarrafSettlement { Id = 1, SarrafId = 1, SettlementDate = new DateTime(2026, 5, 1), ReferenceNumber = "SRF-DRAFT", Status = SarrafSettlementStatus.Draft, RequestedAmountUsd = 1_000m },
            new SarrafSettlement { Id = 2, SarrafId = 1, SettlementDate = new DateTime(2026, 5, 2), ReferenceNumber = "SRF-NOLEDGER", Status = SarrafSettlementStatus.Posted, RequestedAmountUsd = 2_000m },
            new SarrafSettlement { Id = 3, SarrafId = 1, SettlementDate = new DateTime(2026, 5, 3), ReferenceNumber = "SRF-OK", Status = SarrafSettlementStatus.Posted, RequestedAmountUsd = 3_000m, LedgerEntryId = 7 });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.UnconfirmedSarrafSettlement);

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Rows, r => r.Reference == "SRF-OK");
    }

    [Fact]
    public async Task Quality_Inspection_Pending_And_Rejected_Are_Separate_Categories()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.QualityInspections.AddRange(
            new QualityInspection { Id = 1, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-P", SampleDate = new DateTime(2026, 5, 1), Status = QualityInspectionStatus.Pending },
            new QualityInspection { Id = 2, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-R", SampleDate = new DateTime(2026, 5, 2), Status = QualityInspectionStatus.Rejected, RejectionReason = "گوگرد بالا" },
            new QualityInspection { Id = 3, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-A", SampleDate = new DateTime(2026, 5, 3), Status = QualityInspectionStatus.Accepted });
        await db.SaveChangesAsync();

        var service = NewService(db);

        var pending = await service.BuildDiscrepancyPageAsync(ReconciliationDiscrepancyCategory.QualityInspectionPending);
        Assert.Equal(1, pending.TotalCount);
        Assert.Equal("QI-P", pending.Rows[0].Reference);

        var rejected = await service.BuildDiscrepancyPageAsync(ReconciliationDiscrepancyCategory.QualityInspectionRejected);
        Assert.Equal(1, rejected.TotalCount);
        Assert.Equal("QI-R", rejected.Rows[0].Reference);
        Assert.Equal("گوگرد بالا", rejected.Rows[0].Detail);
    }

    [Fact]
    public async Task Finished_Inspection_Without_Result_Document_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.QualityInspections.AddRange(
            new QualityInspection { Id = 1, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-NODOC", SampleDate = new DateTime(2026, 5, 1), Status = QualityInspectionStatus.Accepted },
            new QualityInspection
            {
                Id = 2,
                ProductId = 1,
                LaboratoryName = "Lab",
                ResultNumber = "QI-DOC",
                SampleDate = new DateTime(2026, 5, 2),
                Status = QualityInspectionStatus.Accepted,
                Documents =
                {
                    new QualityInspectionDocument
                    {
                        Id = 1,
                        OriginalFileName = "r.pdf",
                        StoredFileName = "r.pdf",
                        FilePath = "uploads/quality-inspections/2/r.pdf"
                    }
                }
            },
            // Pending هنوز نتیجه ندارد، پس نبود سند برای آن مغایرت نیست.
            new QualityInspection { Id = 3, ProductId = 1, LaboratoryName = "Lab", ResultNumber = "QI-PEND", SampleDate = new DateTime(2026, 5, 3), Status = QualityInspectionStatus.Pending });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.QualityInspectionWithoutResultDocument);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("QI-NODOC", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Incomplete_Customs_Document_And_Incomplete_Lineage_Are_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            DeclarationReference = "CD-1",
            DeclarationDate = new DateTime(2026, 5, 1),
            TotalUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "W-1",
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = 50m
        });
        await db.SaveChangesAsync();

        var service = NewService(db);

        var customs = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.IncompleteCustomsDocument);
        Assert.Equal(1, customs.TotalCount);

        var lineage = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.IncompleteContractOrShipmentLineage);
        Assert.Equal(1, lineage.TotalCount);
        Assert.Equal("W-1", lineage.Rows[0].Reference);
    }

    [Fact]
    public async Task Transport_Leg_With_Freight_But_No_Expense_Is_Reported()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "FRT", Name = "Freight" });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg { Id = 1, ProductId = 1, SourceTerminalId = 1, TransportType = LoadingTransportType.Wagon, WagonNumber = "W-NOEXP", LoadedDate = new DateTime(2026, 5, 1), QuantityMt = 50m, FreightAmount = 400m },
            new InventoryTransportLeg { Id = 2, ProductId = 1, SourceTerminalId = 1, TransportType = LoadingTransportType.Wagon, WagonNumber = "W-OK", LoadedDate = new DateTime(2026, 5, 2), QuantityMt = 50m, FreightAmount = 400m });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            TransportLegId = 2,
            ExpenseDate = new DateTime(2026, 5, 2),
            AmountUsd = 400m
        });
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.OperationalDocumentWithoutLedger);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("W-NOEXP", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Every_Category_Has_A_Count_And_Paging_Is_Independent_Per_Category()
    {
        await using var db = NewDb();
        SeedReference(db);
        for (var i = 1; i <= 7; i++)
        {
            db.SalesTransactions.Add(NewSale(i));
        }
        await db.SaveChangesAsync();

        var service = NewService(db);

        var counts = await service.BuildDiscrepancyCountsAsync();
        Assert.Equal(ReconciliationDiscrepancyText.All.Count, counts.Count);
        Assert.Equal(7, counts.Single(c => c.Category == ReconciliationDiscrepancyCategory.SaleWithoutCogs).Count);

        var first = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs, page: 1, pageSize: 3);
        Assert.Equal(7, first.TotalCount);
        Assert.Equal(3, first.Rows.Count);
        Assert.Equal(3, first.PageCount);
        Assert.False(first.HasPrevious);
        Assert.True(first.HasNext);

        var last = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs, page: 3, pageSize: 3);
        Assert.Single(last.Rows);
        Assert.True(last.HasPrevious);
        Assert.False(last.HasNext);

        // درخواست صفحه‌ای فراتر از آخرین صفحه به آخرین صفحهٔ موجود برمی‌گردد، نه خطا.
        var beyond = await service.BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs, page: 99, pageSize: 3);
        Assert.Equal(3, beyond.Page);
    }

    [Fact]
    public async Task Date_Filter_Is_Applied_To_The_Category_Query()
    {
        await using var db = NewDb();
        SeedReference(db);
        var early = NewSale(1);
        early.SaleDate = new DateTime(2026, 1, 10);
        var late = NewSale(2);
        late.SaleDate = new DateTime(2026, 6, 10);
        db.SalesTransactions.AddRange(early, late);
        await db.SaveChangesAsync();

        var page = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs,
            fromDate: new DateTime(2026, 6, 1));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("INV-2", page.Rows[0].Reference);
    }

    [Fact]
    public async Task Discrepancies_Page_And_Export_Read_The_Same_Service_Rows()
    {
        await using var db = NewDb();
        SeedReference(db);
        db.SalesTransactions.AddRange(NewSale(1), NewSale(2));
        await db.SaveChangesAsync();

        var controller = new ReconciliationController(db);

        var view = Assert.IsType<ViewResult>(await controller.Discrepancies(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs));
        var model = Assert.IsType<ReconciliationDiscrepanciesViewModel>(view.Model);

        Assert.NotNull(model.Selected);
        Assert.Equal(2, model.Selected!.TotalCount);

        var direct = await NewService(db).BuildDiscrepancyPageAsync(
            ReconciliationDiscrepancyCategory.SaleWithoutCogs);
        Assert.Equal(direct.TotalCount, model.Selected.TotalCount);
        Assert.Equal(
            direct.Rows.Select(r => r.Reference).ToArray(),
            model.Selected.Rows.Select(r => r.Reference).ToArray());
    }
}
