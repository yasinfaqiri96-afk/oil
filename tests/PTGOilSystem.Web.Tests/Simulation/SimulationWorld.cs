using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;

namespace PTGOilSystem.Web.Tests.Simulation;

/// <summary>
/// دادهٔ «۱۲ ماه بهره‌برداری» یک شرکت واردات و توزیع نفت در افغانستان.
///
/// این داده عمداً deterministic است (بذر ثابت) تا هر اجرا دقیقاً همان اعداد را بسازد و
/// اختلاف‌های گزارش‌شده قابل بازتولید باشند. شکلِ رکوردها همان چیزی است که مسیرهای واقعیِ
/// نوشتن تولید می‌کنند: هر فروش یک InventoryMovement خروجی و یک LedgerEntry «Sale»،
/// هر مصرف یک LedgerEntry «Expense»، هر پرداخت یک LedgerEntry با SourceType برابر
/// PaymentKind، هر رسید یک LedgerEntry «Loading» و یک InventoryMovement ورودی.
/// </summary>
public sealed class SimulationWorld
{
    public const int Seed = 20260101;

    public static readonly DateTime StartDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public const int Months = 12;

    private readonly Random _random = new(Seed);
    private readonly Dictionary<(string, DateTime), decimal> _rateCache = [];

    public List<int> CompanyIds { get; } = [];
    public List<int> ProductIds { get; } = [];
    public List<int> TerminalIds { get; } = [];
    public List<(int TankId, int TerminalId, int ProductId)> Tanks { get; } = [];
    public List<int> SupplierIds { get; } = [];
    public List<int> CustomerIds { get; } = [];
    public List<int> ServiceProviderIds { get; } = [];
    public List<int> PartnerIds { get; } = [];
    public List<int> CashAccountIds { get; } = [];
    public List<int> LocationIds { get; } = [];
    public List<int> TruckIds { get; } = [];
    public List<int> DriverIds { get; } = [];
    public List<int> ExpenseTypeIds { get; } = [];

    public List<int> PersonalPurchaseContractIds { get; } = [];
    public List<int> PartnershipPurchaseContractIds { get; } = [];
    public List<int> SaleContractIds { get; } = [];

    public sealed record MonthlyVolume(
        int Month,
        int Loadings,
        int Sales,
        int Expenses,
        int Payments,
        int Dispatches,
        int Losses);

    public List<MonthlyVolume> Volumes { get; } = [];

    private static DateTime BusinessDate(int year, int month, int day)
        => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    private decimal Between(decimal min, decimal max, int decimals)
        => decimal.Round(min + ((decimal)_random.NextDouble() * (max - min)), decimals, MidpointRounding.AwayFromZero);

    // ---------------------------------------------------------------- master data

    public async Task SeedMasterDataAsync(ApplicationDbContext db)
    {
        // چند ارز و واحد را خودِ Migration ساخته؛ فقط موارد نبوده اضافه می‌شود.
        var existingCurrencyCodes = await db.Currencies.Select(c => c.Code).ToListAsync();
        foreach (var (code, name, symbol) in new[]
                 {
                     ("USD", "US Dollar", "$"),
                     ("AFN", "Afghani", "AFN"),
                     ("RUB", "Russian Rouble", "RUB"),
                     ("IRR", "Iranian Rial", "IRR")
                 })
        {
            if (!existingCurrencyCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                db.Currencies.Add(new Currency { Code = code, Name = name, Symbol = symbol });
        }

        var existingUnitCodes = await db.Units.Select(u => u.Code).ToListAsync();
        if (!existingUnitCodes.Contains("MT", StringComparer.OrdinalIgnoreCase))
            db.Units.Add(new Unit { Code = "MT", Name = "Metric Ton", IsBaseUnit = true, ConversionFactorToBase = 1m });
        if (!existingUnitCodes.Contains("L", StringComparer.OrdinalIgnoreCase))
            db.Units.Add(new Unit { Code = "L", Name = "Litre", BaseUnitCode = "MT", ConversionFactorToBase = 0.00084m });

        var companies = new[]
        {
            new Company { Code = "PTG", Name = "PTG Petroleum", Country = "AF", IsSystemOwner = true },
            new Company { Code = "PTG-HRT", Name = "PTG Herat Licence", Country = "AF" },
            new Company { Code = "PTG-MZR", Name = "PTG Mazar Licence", Country = "AF" }
        };
        db.Companies.AddRange(companies);

        var products = new[]
        {
            new Product { Code = "GO", Name = "Gas Oil", NamePersian = "دیزل" },
            new Product { Code = "MG", Name = "Motor Gasoline", NamePersian = "پترول" },
            new Product { Code = "LPG", Name = "Liquefied Petroleum Gas", NamePersian = "گاز مایع" }
        };
        db.Products.AddRange(products);

        var terminals = new[]
        {
            new Terminal { Code = "HRT", Name = "Herat Terminal", Location = "Herat" },
            new Terminal { Code = "HRN", Name = "Hairatan Terminal", Location = "Balkh" },
            new Terminal { Code = "TRG", Name = "Torghundi Terminal", Location = "Herat" }
        };
        db.Terminals.AddRange(terminals);

        var locations = new[]
        {
            new Location { Code = "KBL", Name = "Kabul", Country = "AF", Kind = "Destination" },
            new Location { Code = "HRT", Name = "Herat", Country = "AF", Kind = "Destination" },
            new Location { Code = "MZR", Name = "Mazar-e-Sharif", Country = "AF", Kind = "Destination" },
            new Location { Code = "TKM", Name = "Turkmenbashi", Country = "TM", Kind = "Origin" },
            new Location { Code = "UZB", Name = "Termez", Country = "UZ", Kind = "Origin" }
        };
        db.Locations.AddRange(locations);

        for (var i = 1; i <= 6; i++)
        {
            db.Suppliers.Add(new Supplier
            {
                Code = $"SUP-{i:00}",
                Name = $"Supplier {i}",
                Country = i % 2 == 0 ? "TM" : "UZ"
            });
        }

        for (var i = 1; i <= 12; i++)
        {
            db.Customers.Add(new Customer { Code = $"CUS-{i:00}", Name = $"Customer {i}", Country = "AF" });
        }

        for (var i = 1; i <= 5; i++)
        {
            db.ServiceProviders.Add(new ServiceProviderEntity
            {
                Code = $"SRV-{i:00}",
                Name = $"Service Provider {i}",
                ProviderType = (ServiceProviderType)(((i - 1) % 10) + 1)
            });
        }

        for (var i = 1; i <= 4; i++)
        {
            db.Partners.Add(new Partner { Code = $"PRT-{i:00}", Name = $"Partner {i}", Country = "AF" });
        }

        var cashCurrencies = new[] { "USD", "AFN", "USD", "AFN", "RUB" };
        for (var i = 1; i <= 5; i++)
        {
            db.CashAccounts.Add(new CashAccount
            {
                Code = $"CASH-{i:00}",
                Name = $"Cash/Bank {i}",
                Currency = cashCurrencies[i - 1],
                AccountType = i % 2 == 0 ? CashAccountType.Bank : CashAccountType.Cash
            });
        }

        for (var i = 1; i <= 120; i++)
        {
            db.Trucks.Add(new Truck { PlateNumber = $"HRT-{i:0000}", MaxLoadMt = 30m });
        }

        for (var i = 1; i <= 40; i++)
        {
            db.Drivers.Add(new Driver { FullName = $"Driver {i}", Phone = $"+9370000{i:0000}" });
        }

        var expenseTypes = new[]
        {
            ("FRT", "Freight"), ("STO", "Storage"), ("CUS", "Customs Duty"), ("CLR", "Clearance"),
            ("LAB", "Loading Labour"), ("INS", "Inspection"), ("COM", "Commission"), ("RAI", "Railway"),
            ("OTH", "Other Operational"), ("BNK", "Bank Charges")
        };
        foreach (var (code, name) in expenseTypes)
        {
            db.ExpenseTypes.Add(new ExpenseType
            {
                Code = code,
                Name = name,
                NamePersian = name,
                Category = "Operational"
            });
        }

        var role = new Role { Name = "Operator", CanManageData = true };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        for (var i = 1; i <= 20; i++)
        {
            db.Users.Add(new User
            {
                Username = $"user{i:00}",
                FullName = $"User {i}",
                PasswordHash = "x",
                RoleId = role.Id
            });
        }

        var tankIndex = 1;
        foreach (var terminal in terminals)
        {
            foreach (var product in products)
            {
                db.StorageTanks.Add(new StorageTank
                {
                    TerminalId = terminal.Id,
                    TankCode = $"TK-{tankIndex:000}",
                    DisplayName = $"{terminal.Code} {product.Code}",
                    ProductId = product.Id,
                    CapacityMt = 25_000m
                });
                tankIndex++;
            }
        }

        await db.SaveChangesAsync();

        CompanyIds.AddRange(companies.Select(c => c.Id));
        ProductIds.AddRange(products.Select(p => p.Id));
        TerminalIds.AddRange(terminals.Select(t => t.Id));
        LocationIds.AddRange(locations.Select(l => l.Id));

        SupplierIds.AddRange(await db.Suppliers.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
        CustomerIds.AddRange(await db.Customers.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync());
        ServiceProviderIds.AddRange(await db.ServiceProviders.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
        PartnerIds.AddRange(await db.Partners.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
        CashAccountIds.AddRange(await db.CashAccounts.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync());
        TruckIds.AddRange(await db.Trucks.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync());
        DriverIds.AddRange(await db.Drivers.OrderBy(d => d.Id).Select(d => d.Id).ToListAsync());
        ExpenseTypeIds.AddRange(await db.ExpenseTypes.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());

        var tanks = await db.StorageTanks
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.TerminalId, t.ProductId })
            .ToListAsync();
        Tanks.AddRange(tanks.Select(t => (t.Id, t.TerminalId, t.ProductId!.Value)));

        var fxRows = new List<DailyFxRate>();
        for (var day = 0; day < 460; day++)
        {
            var date = StartDate.AddDays(day);
            var afnPerUsd = 70m + (decimal)Math.Round(Math.Sin(day / 11.0) * 4.0, 4);
            var rubPerUsd = 92m + (decimal)Math.Round(Math.Cos(day / 9.0) * 6.0, 4);
            fxRows.Add(new DailyFxRate
            {
                BaseCurrency = "AFN",
                QuoteCurrency = "USD",
                RateDate = date,
                Rate = decimal.Round(1m / afnPerUsd, 12, MidpointRounding.AwayFromZero),
                Source = "Simulation"
            });
            fxRows.Add(new DailyFxRate
            {
                BaseCurrency = "RUB",
                QuoteCurrency = "USD",
                RateDate = date,
                Rate = decimal.Round(1m / rubPerUsd, 12, MidpointRounding.AwayFromZero),
                Source = "Simulation"
            });
        }

        db.DailyFxRates.AddRange(fxRows);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- contracts

    public async Task SeedContractsAsync(ApplicationDbContext db)
    {
        var contracts = new List<Contract>();

        for (var i = 1; i <= 30; i++)
        {
            contracts.Add(new Contract
            {
                ContractNumber = $"PUR-P-{i:000}",
                ContractName = $"Personal Purchase {i}",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Active,
                OwnershipType = ContractOwnershipType.Personal,
                CompanyId = Pick(CompanyIds),
                ProductId = Pick(ProductIds),
                SupplierId = Pick(SupplierIds),
                ContractDate = StartDate.AddDays(_random.Next(0, 300)),
                PricingMethod = PricingMethod.Fixed,
                QuantityMt = Between(3000m, 20000m, 4),
                UnitPriceUsd = Between(420m, 720m, 4),
                Currency = "USD"
            });
        }

        for (var i = 1; i <= 30; i++)
        {
            contracts.Add(new Contract
            {
                ContractNumber = $"PUR-S-{i:000}",
                ContractName = $"Partnership Purchase {i}",
                ContractType = ContractType.Purchase,
                Status = ContractStatus.Active,
                OwnershipType = ContractOwnershipType.Partnership,
                CompanyId = Pick(CompanyIds),
                ProductId = Pick(ProductIds),
                SupplierId = Pick(SupplierIds),
                ContractDate = StartDate.AddDays(_random.Next(0, 300)),
                PricingMethod = PricingMethod.Fixed,
                QuantityMt = Between(3000m, 20000m, 4),
                UnitPriceUsd = Between(420m, 720m, 4),
                Currency = "USD"
            });
        }

        for (var i = 1; i <= 20; i++)
        {
            contracts.Add(new Contract
            {
                ContractNumber = $"SAL-{i:000}",
                ContractName = $"Sale Contract {i}",
                ContractType = ContractType.Sale,
                Status = ContractStatus.Active,
                OwnershipType = ContractOwnershipType.Personal,
                CompanyId = Pick(CompanyIds),
                ProductId = Pick(ProductIds),
                CustomerId = Pick(CustomerIds),
                DestinationLocationId = Pick(LocationIds),
                ContractDate = StartDate.AddDays(_random.Next(0, 300)),
                PricingMethod = PricingMethod.Fixed,
                QuantityMt = Between(1000m, 8000m, 4),
                UnitPriceUsd = Between(520m, 860m, 4),
                Currency = "USD"
            });
        }

        db.Contracts.AddRange(contracts);
        await db.SaveChangesAsync();

        PersonalPurchaseContractIds.AddRange(contracts
            .Where(c => c.ContractType == ContractType.Purchase && c.OwnershipType == ContractOwnershipType.Personal)
            .Select(c => c.Id));
        PartnershipPurchaseContractIds.AddRange(contracts
            .Where(c => c.OwnershipType == ContractOwnershipType.Partnership)
            .Select(c => c.Id));
        SaleContractIds.AddRange(contracts
            .Where(c => c.ContractType == ContractType.Sale)
            .Select(c => c.Id));

        var index = 0;
        foreach (var contractId in PartnershipPurchaseContractIds)
        {
            var partnerA = PartnerIds[index % PartnerIds.Count];
            var partnerB = PartnerIds[(index + 1) % PartnerIds.Count];
            // PTG-P0-03 — نخستین بازهٔ سهم از تاریخ خودِ قرارداد آغاز می‌شود.
            var shareStart = contracts.Single(c => c.Id == contractId).ContractDate.Date;
            db.ContractPartners.Add(new ContractPartner
            {
                ContractId = contractId,
                PartnerId = partnerA,
                SharePercent = 50m,
                EffectiveFrom = shareStart
            });
            db.ContractPartners.Add(new ContractPartner
            {
                ContractId = contractId,
                PartnerId = partnerB,
                SharePercent = 50m,
                EffectiveFrom = shareStart
            });

            contracts.Single(c => c.Id == contractId).SaleProceedsHolderPartnerId = partnerA;
            index++;
        }

        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- 12 months

    public async Task RunTwelveMonthsAsync(ApplicationDbContext db)
    {
        var purchaseContracts = await db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase)
            .Select(c => new { c.Id, c.ProductId, c.SupplierId, c.UnitPriceUsd })
            .ToListAsync();

        var partnerByContract = await db.ContractPartners
            .AsNoTracking()
            .GroupBy(cp => cp.ContractId)
            .Select(g => new { ContractId = g.Key, PartnerIds = g.Select(x => x.PartnerId).ToList() })
            .ToDictionaryAsync(x => x.ContractId, x => x.PartnerIds);

        var invoiceCounter = 0;
        var loadingCounter = 0;

        for (var monthOffset = 0; monthOffset < Months; monthOffset++)
        {
            var monthStart = StartDate.AddMonths(monthOffset);
            var year = monthStart.Year;
            var month = monthStart.Month;

            var receiptsThisMonth =
                new List<(int ContractId, int ProductId, int TankId, int TerminalId, DateTime Date, decimal Qty)>();

            var loadings = new List<LoadingRegister>();
            var loadingLedgers = new List<LedgerEntry>();

            // ---- 1) بارگیری ----------------------------------------------------
            for (var i = 0; i < 100; i++)
            {
                var contract = purchaseContracts[_random.Next(purchaseContracts.Count)];
                var loadingDate = BusinessDate(year, month, _random.Next(1, 28));
                var loadedQty = Between(200m, 900m, 4);
                loadingCounter++;

                loadings.Add(new LoadingRegister
                {
                    ContractId = contract.Id,
                    ProductId = contract.ProductId,
                    OriginLocationId = Pick(LocationIds),
                    TransportType = LoadingTransportType.Wagon,
                    LoadingDate = loadingDate,
                    LoadedQuantityMt = loadedQty,
                    BillOfLadingNumber = $"BOL-{year}{month:00}-{loadingCounter:0000}",
                    RwbNo = $"RWB-{year}{month:00}-{loadingCounter:0000}",
                    ImportUniqueKey = $"SIM|{contract.Id}|{loadingCounter}",
                    LoadingPriceUsd = contract.UnitPriceUsd,
                    SettlementCurrencyCode = "USD"
                });
            }

            db.LoadingRegisters.AddRange(loadings);
            await db.SaveChangesAsync();

            // ---- 2) رسید ورود به موجودی + حرکت ورودی + بدهی تأمین‌کننده ---------
            var receipts = new List<LoadingReceipt>();
            var receiptMeta = new List<(LoadingReceipt Receipt, LoadingRegister Loading, int TankId, int TerminalId)>();
            foreach (var loading in loadings)
            {
                var tank = Tanks.First(t => t.ProductId == loading.ProductId);
                var lossFactor = loading.Id % 25 == 0 ? Between(0.03m, 0.08m, 6) : Between(0m, 0.006m, 6);
                var receivedQty = decimal.Round(
                    loading.LoadedQuantityMt * (1m - lossFactor),
                    4,
                    MidpointRounding.AwayFromZero);
                var receiptDate = loading.LoadingDate.AddDays(_random.Next(1, 6));

                var receipt = new LoadingReceipt
                {
                    LoadingRegisterId = loading.Id,
                    ReceiptDestination = LoadingReceiptDestination.ToInventory,
                    TerminalId = tank.TerminalId,
                    StorageTankId = tank.TankId,
                    ReceiptDate = receiptDate,
                    ReceivedQuantityMt = receivedQty,
                    ReferenceDocument = loading.BillOfLadingNumber
                };
                receipts.Add(receipt);
                receiptMeta.Add((receipt, loading, tank.TankId, tank.TerminalId));
            }

            db.LoadingReceipts.AddRange(receipts);
            await db.SaveChangesAsync();

            var inboundMovements = new List<InventoryMovement>();
            foreach (var (receipt, loading, tankId, terminalId) in receiptMeta)
            {
                var contract = purchaseContracts.Single(c => c.Id == loading.ContractId);
                inboundMovements.Add(new InventoryMovement
                {
                    ProductId = loading.ProductId,
                    ContractId = loading.ContractId,
                    TerminalId = terminalId,
                    StorageTankId = tankId,
                    LoadingReceiptId = receipt.Id,
                    Direction = MovementDirection.In,
                    MovementDate = receipt.ReceiptDate,
                    QuantityMt = receipt.ReceivedQuantityMt,
                    ReferenceDocument = loading.BillOfLadingNumber,
                    Notes = $"ReceiptId={receipt.Id}"
                });

                var amountUsd = decimal.Round(
                    loading.LoadedQuantityMt * (contract.UnitPriceUsd ?? 0m),
                    4,
                    MidpointRounding.AwayFromZero);
                loadingLedgers.Add(new LedgerEntry
                {
                    EntryDate = loading.LoadingDate,
                    Side = LedgerSide.Credit,
                    AmountUsd = amountUsd,
                    Currency = "USD",
                    SourceAmount = amountUsd,
                    SourceCurrencyCode = "USD",
                    AppliedFxRateToUsd = 1m,
                    AppliedCurrencyPerUsdRate = 1m,
                    Description = $"بارگیری {loading.BillOfLadingNumber}",
                    SourceType = "Loading",
                    SourceId = loading.Id,
                    Reference = loading.BillOfLadingNumber,
                    ContractId = loading.ContractId,
                    SupplierId = contract.SupplierId
                });

                receiptsThisMonth.Add((
                    loading.ContractId,
                    loading.ProductId,
                    tankId,
                    terminalId,
                    receipt.ReceiptDate,
                    receipt.ReceivedQuantityMt));
            }

            db.InventoryMovements.AddRange(inboundMovements);
            db.LedgerEntries.AddRange(loadingLedgers);
            await db.SaveChangesAsync();

            // ---- 3) فروش از مخزن ------------------------------------------------
            var sales = new List<SalesTransaction>();
            var salePlan = new List<(int ContractId, int ProductId, int TankId, int TerminalId, DateTime Date, decimal Qty)>();
            for (var i = 0; i < 125 && receiptsThisMonth.Count > 0; i++)
            {
                var source = receiptsThisMonth[_random.Next(receiptsThisMonth.Count)];
                var saleQty = decimal.Round(source.Qty * Between(0.15m, 0.55m, 4), 4, MidpointRounding.AwayFromZero);
                if (saleQty <= 0m)
                {
                    continue;
                }

                var saleDate = source.Date.AddDays(_random.Next(0, 20));
                invoiceCounter++;
                var unitPrice = Between(520m, 900m, 4);
                sales.Add(new SalesTransaction
                {
                    CompanyId = Pick(CompanyIds),
                    ContractId = Pick(SaleContractIds),
                    SourcePurchaseContractId = source.ContractId,
                    CustomerId = Pick(CustomerIds),
                    ProductId = source.ProductId,
                    DestinationLocationId = Pick(LocationIds),
                    SaleStage = SaleStage.TerminalStock,
                    InvoiceNumber = $"INV-{year}{month:00}-{invoiceCounter:00000}",
                    SaleDate = saleDate,
                    QuantityMt = saleQty,
                    Currency = "USD",
                    UnitPriceInCurrency = unitPrice,
                    AppliedFxRateToUsd = 1m,
                    UnitPriceUsd = unitPrice,
                    TotalInCurrency = decimal.Round(saleQty * unitPrice, 4, MidpointRounding.AwayFromZero),
                    TotalUsd = decimal.Round(saleQty * unitPrice, 4, MidpointRounding.AwayFromZero)
                });
                salePlan.Add((source.ContractId, source.ProductId, source.TankId, source.TerminalId, saleDate, saleQty));
            }

            db.SalesTransactions.AddRange(sales);
            await db.SaveChangesAsync();

            var saleMovements = new List<InventoryMovement>();
            var saleLedgers = new List<LedgerEntry>();
            for (var i = 0; i < sales.Count; i++)
            {
                var sale = sales[i];
                var plan = salePlan[i];
                saleMovements.Add(new InventoryMovement
                {
                    ProductId = plan.ProductId,
                    ContractId = plan.ContractId,
                    TerminalId = plan.TerminalId,
                    StorageTankId = plan.TankId,
                    SalesTransactionId = sale.Id,
                    Direction = MovementDirection.Out,
                    MovementDate = plan.Date,
                    QuantityMt = plan.Qty,
                    ReferenceDocument = sale.InvoiceNumber,
                    Notes = $"SaleId={sale.Id}"
                });

                saleLedgers.Add(new LedgerEntry
                {
                    EntryDate = sale.SaleDate,
                    Side = LedgerSide.Credit,
                    AmountUsd = sale.TotalUsd,
                    Currency = "USD",
                    SourceAmount = sale.TotalInCurrency,
                    SourceCurrencyCode = "USD",
                    AppliedFxRateToUsd = 1m,
                    AppliedCurrencyPerUsdRate = 1m,
                    Description = $"ثبت فروش فاکتور {sale.InvoiceNumber}",
                    SourceType = "Sale",
                    SourceId = sale.Id,
                    Reference = sale.InvoiceNumber,
                    ContractId = sale.SourcePurchaseContractId,
                    CustomerId = sale.CustomerId
                });
            }

            db.InventoryMovements.AddRange(saleMovements);
            db.LedgerEntries.AddRange(saleLedgers);
            await db.SaveChangesAsync();

            // ---- 4) مصارف ------------------------------------------------------
            var expenses = new List<ExpenseTransaction>();
            var expenseRates = new List<decimal>();
            for (var i = 0; i < 125; i++)
            {
                var contractId = purchaseContracts[_random.Next(purchaseContracts.Count)].Id;
                var date = BusinessDate(year, month, _random.Next(1, 29));
                var currency = i % 4 == 0 ? "AFN" : "USD";
                var amount = currency == "AFN" ? Between(20_000m, 900_000m, 2) : Between(200m, 30_000m, 2);
                var rate = currency == "AFN" ? await ResolveRateAsync(db, "AFN", date) : 1m;

                expenses.Add(new ExpenseTransaction
                {
                    ExpenseTypeId = Pick(ExpenseTypeIds),
                    ContractId = contractId,
                    ServiceProviderId = Pick(ServiceProviderIds),
                    ExpenseDate = date,
                    Amount = amount,
                    Currency = currency,
                    AppliedFxRateToUsd = rate,
                    AmountUsd = decimal.Round(amount * rate, 4, MidpointRounding.AwayFromZero),
                    Description = $"Operational expense {year}-{month:00}-{i:000}"
                });
                expenseRates.Add(rate);
            }

            db.ExpenseTransactions.AddRange(expenses);
            await db.SaveChangesAsync();

            var expenseLedgers = new List<LedgerEntry>();
            for (var i = 0; i < expenses.Count; i++)
            {
                var expense = expenses[i];
                var rate = expenseRates[i];
                expenseLedgers.Add(new LedgerEntry
                {
                    EntryDate = expense.ExpenseDate,
                    Side = LedgerSide.Debit,
                    AmountUsd = expense.AmountUsd,
                    Currency = "USD",
                    SourceAmount = expense.Amount,
                    SourceCurrencyCode = expense.Currency,
                    AppliedFxRateToUsd = rate,
                    AppliedCurrencyPerUsdRate = rate == 0m
                        ? null
                        : decimal.Round(1m / rate, 12, MidpointRounding.AwayFromZero),
                    Description = expense.Description!,
                    SourceType = "Expense",
                    SourceId = expense.Id,
                    Reference = $"EXP-{expense.Id}",
                    ContractId = expense.ContractId,
                    ServiceProviderId = expense.ServiceProviderId
                });
            }

            db.LedgerEntries.AddRange(expenseLedgers);
            await db.SaveChangesAsync();

            // ---- 5) پرداخت/دریافت (شامل تأمین مالی شرکا) -------------------------
            var payments = new List<PaymentTransaction>();
            var paymentRates = new List<decimal>();
            for (var i = 0; i < 125; i++)
            {
                var date = BusinessDate(year, month, _random.Next(1, 29));
                var isPartnerFunding = i % 5 == 0;
                var contractId = isPartnerFunding
                    ? PartnershipPurchaseContractIds[_random.Next(PartnershipPurchaseContractIds.Count)]
                    : purchaseContracts[_random.Next(purchaseContracts.Count)].Id;

                var currency = i % 3 == 0 ? "AFN" : "USD";
                var amount = currency == "AFN" ? Between(50_000m, 5_000_000m, 2) : Between(500m, 120_000m, 2);
                var rate = currency == "AFN" ? await ResolveRateAsync(db, "AFN", date) : 1m;

                var kind = isPartnerFunding
                    ? PaymentKind.SupplierPayment
                    : (i % 4) switch
                    {
                        0 => PaymentKind.CustomerReceipt,
                        1 => PaymentKind.SupplierPayment,
                        2 => PaymentKind.ExpensePayment,
                        _ => PaymentKind.ServiceProviderPayment
                    };
                var direction = kind == PaymentKind.CustomerReceipt ? PaymentDirection.In : PaymentDirection.Out;

                int? paidByPartnerId = null;
                if (isPartnerFunding && partnerByContract.TryGetValue(contractId, out var contractPartners))
                {
                    paidByPartnerId = contractPartners[i % contractPartners.Count];
                }

                payments.Add(new PaymentTransaction
                {
                    PaymentDate = date,
                    Direction = direction,
                    PaymentKind = kind,
                    CompanyId = Pick(CompanyIds),
                    CashAccountId = isPartnerFunding ? null : Pick(CashAccountIds),
                    FundingSource = isPartnerFunding ? PaymentFundingSource.Partner : PaymentFundingSource.Company,
                    PaidByPartnerId = paidByPartnerId,
                    ContractId = contractId,
                    SupplierId = kind == PaymentKind.SupplierPayment ? Pick(SupplierIds) : null,
                    CustomerId = kind == PaymentKind.CustomerReceipt ? Pick(CustomerIds) : null,
                    ServiceProviderId = kind == PaymentKind.ServiceProviderPayment ? Pick(ServiceProviderIds) : null,
                    Amount = amount,
                    Currency = currency,
                    AppliedFxRateToUsd = rate,
                    AmountUsd = decimal.Round(amount * rate, 4, MidpointRounding.AwayFromZero),
                    Reference = $"PAY-{year}{month:00}-{i:000}",
                    Description = $"Payment {year}-{month:00}-{i:000}"
                });
                paymentRates.Add(rate);
            }

            db.PaymentTransactions.AddRange(payments);
            await db.SaveChangesAsync();

            var paymentLedgers = new List<LedgerEntry>();
            for (var i = 0; i < payments.Count; i++)
            {
                var payment = payments[i];
                var rate = paymentRates[i];
                paymentLedgers.Add(new LedgerEntry
                {
                    EntryDate = payment.PaymentDate,
                    Side = payment.Direction == PaymentDirection.Out ? LedgerSide.Debit : LedgerSide.Credit,
                    AmountUsd = payment.AmountUsd,
                    Currency = "USD",
                    SourceAmount = payment.Amount,
                    SourceCurrencyCode = payment.Currency,
                    AppliedFxRateToUsd = rate,
                    AppliedCurrencyPerUsdRate = rate == 0m
                        ? null
                        : decimal.Round(1m / rate, 12, MidpointRounding.AwayFromZero),
                    Description = payment.Description!,
                    SourceType = payment.PaymentKind.ToString(),
                    SourceId = payment.Id,
                    Reference = payment.Reference,
                    ContractId = payment.ContractId,
                    SupplierId = payment.SupplierId,
                    CustomerId = payment.CustomerId,
                    ServiceProviderId = payment.ServiceProviderId
                });
            }

            db.LedgerEntries.AddRange(paymentLedgers);
            await db.SaveChangesAsync();

            for (var i = 0; i < payments.Count; i++)
            {
                payments[i].LedgerEntryId = paymentLedgers[i].Id;
            }

            await db.SaveChangesAsync();

            // ---- 6) دیسپچ موتر --------------------------------------------------
            var dispatches = new List<TruckDispatch>();
            for (var i = 0; i < 50 && receiptsThisMonth.Count > 0; i++)
            {
                var source = receiptsThisMonth[_random.Next(receiptsThisMonth.Count)];
                var loaded = Between(20m, 30m, 4);
                var discharged = decimal.Round(loaded - Between(0m, 0.35m, 4), 4, MidpointRounding.AwayFromZero);
                dispatches.Add(new TruckDispatch
                {
                    DispatchMode = TruckDispatchMode.FromInventory,
                    ContractId = source.ContractId,
                    ProductId = source.ProductId,
                    TruckId = Pick(TruckIds),
                    DriverId = Pick(DriverIds),
                    DestinationLocationId = Pick(LocationIds),
                    DispatchDate = source.Date.AddDays(_random.Next(0, 15)),
                    Status = DispatchStatus.Delivered,
                    LoadedQuantityMt = loaded,
                    DischargedQuantityMt = discharged,
                    ShortageMt = loaded - discharged,
                    FreightCostUsd = Between(200m, 900m, 2)
                });
            }

            db.TruckDispatches.AddRange(dispatches);
            await db.SaveChangesAsync();

            // ---- 7) ضایعات مخزن -------------------------------------------------
            var lossMovements = new List<InventoryMovement>();
            var lossPlan = new List<(int ContractId, int ProductId, int TankId, int TerminalId, DateTime Date, decimal Qty, decimal Expected)>();
            for (var i = 0; i < 10 && receiptsThisMonth.Count > 0; i++)
            {
                var source = receiptsThisMonth[_random.Next(receiptsThisMonth.Count)];
                var lossQty = Between(0.5m, 5m, 4);
                var lossDate = source.Date.AddDays(_random.Next(1, 20));
                lossMovements.Add(new InventoryMovement
                {
                    ProductId = source.ProductId,
                    ContractId = source.ContractId,
                    TerminalId = source.TerminalId,
                    StorageTankId = source.TankId,
                    Direction = MovementDirection.Out,
                    MovementDate = lossDate,
                    QuantityMt = lossQty,
                    ReferenceDocument = $"LOSS-{year}{month:00}-{i:000}",
                    Notes = "Tank natural loss"
                });
                lossPlan.Add((source.ContractId, source.ProductId, source.TankId, source.TerminalId, lossDate, lossQty, source.Qty));
            }

            db.InventoryMovements.AddRange(lossMovements);
            await db.SaveChangesAsync();

            for (var i = 0; i < lossMovements.Count; i++)
            {
                var plan = lossPlan[i];
                db.LossEvents.Add(new LossEvent
                {
                    Stage = LossEventStage.TankNaturalLoss,
                    ProductId = plan.ProductId,
                    ContractId = plan.ContractId,
                    TerminalId = plan.TerminalId,
                    StorageTankId = plan.TankId,
                    EventDate = plan.Date,
                    ExpectedQuantityMt = plan.Expected,
                    ActualQuantityMt = plan.Expected - plan.Qty,
                    DifferenceQuantityMt = plan.Qty,
                    ChargeableLossMt = plan.Qty,
                    AffectsInventory = true,
                    InventoryMovementId = lossMovements[i].Id,
                    Reference = lossMovements[i].ReferenceDocument
                });
            }

            await db.SaveChangesAsync();

            Volumes.Add(new MonthlyVolume(monthOffset + 1, loadings.Count, sales.Count, expenses.Count, payments.Count, dispatches.Count, lossMovements.Count));
            db.ChangeTracker.Clear();
        }
    }

    private async Task<decimal> ResolveRateAsync(ApplicationDbContext db, string currency, DateTime date)
    {
        if (_rateCache.TryGetValue((currency, date), out var cached))
        {
            return cached;
        }

        var rate = await db.DailyFxRates
            .AsNoTracking()
            .Where(r => r.BaseCurrency == currency && r.QuoteCurrency == "USD" && r.RateDate <= date)
            .OrderByDescending(r => r.RateDate)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync() ?? 1m;

        _rateCache[(currency, date)] = rate;
        return rate;
    }
}
