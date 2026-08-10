using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data.Configurations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;

namespace PTGOilSystem.Web.Data;

public class ApplicationDbContext : DbContext
{
    private readonly ICurrentUserContext? _currentUserContext;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, null) { }

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ICurrentUserContext? currentUserContext)
        : base(options)
        => _currentUserContext = currentUserContext;

    // --- Master Data ---
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ServiceProviderEntity> ServiceProviders => Set<ServiceProviderEntity>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<StorageTank> StorageTanks => Set<StorageTank>();
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<Truck> Trucks => Set<Truck>();
    public DbSet<Wagon> Wagons => Set<Wagon>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<ExpenseType> ExpenseTypes => Set<ExpenseType>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Employee> Employees => Set<Employee>();

    // --- Contracts & Pricing ---
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractPartner> ContractPartners => Set<ContractPartner>();
    public DbSet<ContractAmendment> ContractAmendments => Set<ContractAmendment>();
    public DbSet<ContractPricingRule> ContractPricingRules => Set<ContractPricingRule>();
    public DbSet<DailyPlattsPrice> DailyPlattsPrices => Set<DailyPlattsPrice>();
    public DbSet<DailyFxRate> DailyFxRates => Set<DailyFxRate>();
    public DbSet<PlattsMonthlyManual> PlattsMonthlyManuals => Set<PlattsMonthlyManual>();

    // --- Inventory & Logistics ---
    public DbSet<InventoryBatch> InventoryBatches => Set<InventoryBatch>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<LoadingRegister> LoadingRegisters => Set<LoadingRegister>();
    public DbSet<LoadingReceipt> LoadingReceipts => Set<LoadingReceipt>();
    public DbSet<LoadingReceiptAllocation> LoadingReceiptAllocations => Set<LoadingReceiptAllocation>();
    public DbSet<LoadingExpenseLine> LoadingExpenseLines => Set<LoadingExpenseLine>();
    public DbSet<InventoryTransportLeg> InventoryTransportLegs => Set<InventoryTransportLeg>();
    public DbSet<InventoryTransportBatch> InventoryTransportBatches => Set<InventoryTransportBatch>();
    public DbSet<InventoryTransportLegAllocation> InventoryTransportLegAllocations => Set<InventoryTransportLegAllocation>();
    public DbSet<InventoryTransportReceipt> InventoryTransportReceipts => Set<InventoryTransportReceipt>();
    public DbSet<TruckDispatch> TruckDispatches => Set<TruckDispatch>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentContract> ShipmentContracts => Set<ShipmentContract>();
    public DbSet<ShipmentLoadingAllocation> ShipmentLoadingAllocations => Set<ShipmentLoadingAllocation>();
    public DbSet<DeliveryReceipt> DeliveryReceipts => Set<DeliveryReceipt>();
    public DbSet<LossEvent> LossEvents => Set<LossEvent>();
    public DbSet<SalesTransactionSourceAllocation> SalesTransactionSourceAllocations => Set<SalesTransactionSourceAllocation>();
    public DbSet<LossEventSourceAllocation> LossEventSourceAllocations => Set<LossEventSourceAllocation>();

    // --- Inventory Lineage (Phase 2 — parallel reference layer, augments InventoryMovement) ---
    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();
    public DbSet<InventoryLotMovement> InventoryLotMovements => Set<InventoryLotMovement>();
    public DbSet<SaleLotAllocation> SaleLotAllocations => Set<SaleLotAllocation>();
    public DbSet<LossLotAllocation> LossLotAllocations => Set<LossLotAllocation>();
    public DbSet<ExpenseLotAllocation> ExpenseLotAllocations => Set<ExpenseLotAllocation>();

    // --- Customs & Declarations ---
    public DbSet<CustomsDeclaration> CustomsDeclarations => Set<CustomsDeclaration>();
    public DbSet<CustomsDeclarationItem> CustomsDeclarationItems => Set<CustomsDeclarationItem>();
    public DbSet<CustomsDeclarationDocument> CustomsDeclarationDocuments => Set<CustomsDeclarationDocument>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();
    public DbSet<QualityInspectionDocument> QualityInspectionDocuments => Set<QualityInspectionDocument>();

    // --- Sales & Expenses ---
    public DbSet<SalesTransaction> SalesTransactions => Set<SalesTransaction>();
    public DbSet<ExpenseRule> ExpenseRules => Set<ExpenseRule>();
    public DbSet<ExpenseTransaction> ExpenseTransactions => Set<ExpenseTransaction>();
    public DbSet<ExpenseBatch> ExpenseBatches => Set<ExpenseBatch>();
    public DbSet<SalesBatch> SalesBatches => Set<SalesBatch>();
    public DbSet<PreSaleOrder> PreSaleOrders => Set<PreSaleOrder>();

    // --- Finance & Audit ---
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<Sarraf> Sarrafs => Set<Sarraf>();
    public DbSet<SarrafSettlement> SarrafSettlements => Set<SarrafSettlement>();
    public DbSet<ThreeWaySettlement> ThreeWaySettlements => Set<ThreeWaySettlement>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<ContractBalanceTransfer> ContractBalanceTransfers => Set<ContractBalanceTransfer>();
    public DbSet<SupplierPaymentAllocation> SupplierPaymentAllocations => Set<SupplierPaymentAllocation>();
    public DbSet<SupplierBalanceTransfer> SupplierBalanceTransfers => Set<SupplierBalanceTransfer>();
    public DbSet<SupplierBalanceTransferSource> SupplierBalanceTransferSources => Set<SupplierBalanceTransferSource>();
    public DbSet<CustomerPaymentAllocation> CustomerPaymentAllocations => Set<CustomerPaymentAllocation>();
    public DbSet<CustomerPaymentAllocationApplication> CustomerPaymentAllocationApplications => Set<CustomerPaymentAllocationApplication>();
    public DbSet<SalesCostConsumption> SalesCostConsumptions => Set<SalesCostConsumption>();
    public DbSet<EmployeeSalaryTransaction> EmployeeSalaryTransactions => Set<EmployeeSalaryTransaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // --- Independent Accounting Core (not connected to operational posting) ---
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountingSettings> AccountingSettings => Set<AccountingSettings>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();
    public DbSet<FiscalYearStatusHistory> FiscalYearStatusHistories => Set<FiscalYearStatusHistory>();
    public DbSet<FiscalYearCloseRun> FiscalYearCloseRuns => Set<FiscalYearCloseRun>();
    public DbSet<InventoryAverageCost> InventoryAverageCosts => Set<InventoryAverageCost>();

    // --- Idempotency (duplicate-submit guard; no business logic) ---
    public DbSet<ProcessedFormToken> ProcessedFormTokens => Set<ProcessedFormToken>();

    // --- Owned Operational Assets ---
    public DbSet<OperationalAsset> OperationalAssets => Set<OperationalAsset>();
    public DbSet<AssetOwnershipShare> AssetOwnershipShares => Set<AssetOwnershipShare>();
    public DbSet<AssetRentTransaction> AssetRentTransactions => Set<AssetRentTransaction>();
    public DbSet<AssetRentShare> AssetRentShares => Set<AssetRentShare>();

    // --- Backups (operational safety module; no business/financial logic) ---
    public DbSet<BackupSetting> BackupSettings => Set<BackupSetting>();
    public DbSet<BackupDriveCredential> BackupDriveCredentials => Set<BackupDriveCredential>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();

    public override int SaveChanges()
    {
        PrepareTrackedEntitiesForSave();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareTrackedEntitiesForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntitiesForSave();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntitiesForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- numeric precision (system rule #1: decimal/numeric for all money/qty/rate) ----
        // Money & weights: numeric(18,4)
        ConfigureMoney<Contract>(modelBuilder, c => c.UnitPriceUsd!);
        ConfigureMoney<Contract>(modelBuilder, c => c.UnitPriceInCurrency!);
        ConfigureMoney<Contract>(modelBuilder, c => c.PremiumUsd!);
        ConfigureMoney<Contract>(modelBuilder, c => c.PremiumDiscountUsd!);
        ConfigureMoney<Contract>(modelBuilder, c => c.PlattsManualPriceUsd!);
        ConfigureMoney<Contract>(modelBuilder, c => c.MinimumPriceUsd!);
        ConfigureWeight<Contract>(modelBuilder, c => c.QuantityMt);
        modelBuilder.Entity<Contract>().Property(c => c.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<Contract>().Property(c => c.SettlementCurrencyCode).HasDefaultValue("USD");
        modelBuilder.Entity<Contract>().Property(c => c.RubRatePolicy).HasDefaultValue(RubSettlementRatePolicy.NotApplicable);
        modelBuilder.Entity<Contract>().Property(c => c.ContractRubPerUsdRate).HasColumnType("numeric(18,6)");

        modelBuilder.Entity<ContractPartner>().Property(c => c.SharePercent).HasColumnType("numeric(9,4)");

        ConfigureWeight<ContractAmendment>(modelBuilder, c => c.NewQuantityMt!);
        ConfigureMoney<ContractAmendment>(modelBuilder, c => c.NewUnitPriceUsd!);
        ConfigureMoney<ContractAmendment>(modelBuilder, c => c.NewPremiumUsd!);

        ConfigureMoney<ContractPricingRule>(modelBuilder, c => c.PremiumUsd!);

        ConfigureMoney<DailyPlattsPrice>(modelBuilder, p => p.PriceUsdPerMt);
        // نرخ روز در جهت «ارز → دالر» ذخیره می‌شود. فورم‌ها جهت معکوس («۱ دالر = چند واحد»)
        // را می‌خواهند، و با ۶ رقم اعشار این تبدیل خطای دیدنی می‌ساخت: 1/85 = 0.011765 و
        // برگشتش ۸۵.۰۰۲۱۲۵. با ۱۲ رقم خطا به ~1e-11 می‌رسد و زیر گردکردنِ نمایش گم می‌شود.
        modelBuilder.Entity<DailyFxRate>().Property(r => r.Rate).HasColumnType("numeric(24,12)");
        ConfigureMoney<PlattsMonthlyManual>(modelBuilder, p => p.PriceUsdPerMt);

        ConfigureWeight<StorageTank>(modelBuilder, t => t.CapacityMt);
        ConfigureWeight<Truck>(modelBuilder, t => t.MaxLoadMt!);
        ConfigureWeight<Wagon>(modelBuilder, w => w.CapacityMt!);
        modelBuilder.Entity<Unit>().Property(u => u.ConversionFactorToBase).HasColumnType("numeric(18,10)");
        modelBuilder.Entity<Unit>().Property(u => u.IsBaseUnit).HasDefaultValue(false);
        modelBuilder.Entity<Location>().Property(l => l.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<StorageTank>().Property(t => t.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<Wagon>().Property(w => w.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<ServiceProviderEntity>().Property(p => p.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<OperationalAsset>().Property(a => a.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<InventoryTransportLeg>().ToTable("InventoryTransportLegs");
        modelBuilder.Entity<InventoryTransportBatch>().ToTable("InventoryTransportBatches");
        modelBuilder.Entity<InventoryTransportLegAllocation>().ToTable("InventoryTransportLegAllocations");
        modelBuilder.Entity<LoadingRegister>().HasIndex(l => l.LogisticsServiceProviderId);
        // گارد نهایی ضد ثبت تکراری بارگیری. کلید خودش ContractId را در بر دارد، پس یکتایی
        // فقط داخل همان قرارداد اعمال می‌شود. مقدار null (بارگیری بدون شمارهٔ سند و بدون شمارهٔ
        // حمل، و همهٔ رکوردهای قبلی) در PostgreSQL از یکتایی معاف است و دست‌نخورده می‌ماند.
        modelBuilder.Entity<LoadingRegister>().HasIndex(l => l.ImportUniqueKey).IsUnique();

        ConfigureWeight<InventoryBatch>(modelBuilder, b => b.InitialQuantityMt);
        ConfigureWeight<InventoryMovement>(modelBuilder, m => m.QuantityMt);

        ConfigureWeight<LoadingRegister>(modelBuilder, l => l.LoadedQuantityMt);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.LoadingPriceUsd!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.FreightRateUsdPerMt!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.TransportExpenseUsd!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.WarehouseExpenseUsd!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.OtherExpenseUsd!);
        modelBuilder.Entity<LoadingRegister>().Property(l => l.SettlementCurrencyCode).HasDefaultValue("USD");
        modelBuilder.Entity<LoadingRegister>().Property(l => l.RubRateStatus).HasDefaultValue(RubSettlementRateStatus.NotRequired);
        modelBuilder.Entity<LoadingRegister>().Property(l => l.RubPerUsdRate).HasColumnType("numeric(18,6)");
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.AmountUsdAtRubLock!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.AmountRubAtRubLock!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.SettlementUnitPriceRub!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.SettlementValueRub!);
        ConfigureWeight<LoadingReceipt>(modelBuilder, r => r.ReceivedQuantityMt);
        ConfigureWeight<LoadingReceiptAllocation>(modelBuilder, a => a.QuantityMt);
        ConfigureWeight<InventoryTransportLeg>(modelBuilder, l => l.QuantityMt);
        ConfigureWeight<InventoryTransportLeg>(modelBuilder, l => l.ChargeableQuantityMt!);
        ConfigureWeight<InventoryTransportLeg>(modelBuilder, l => l.CapacityMt!);
        ConfigureMoney<InventoryTransportLeg>(modelBuilder, l => l.FreightAmount!);
        ConfigureMoney<InventoryTransportLeg>(modelBuilder, l => l.PurchaseUnitCostUsd!);
        ConfigureWeight<InventoryTransportBatch>(modelBuilder, b => b.TotalQuantityMt);
        ConfigureWeight<InventoryTransportLegAllocation>(modelBuilder, a => a.QuantityMt);
        modelBuilder.Entity<InventoryTransportReceipt>().ToTable("InventoryTransportReceipts");
        ConfigureWeight<InventoryTransportReceipt>(modelBuilder, r => r.ReceivedQuantityMt);
        ConfigureWeight<InventoryTransportReceipt>(modelBuilder, r => r.ShortageQuantityMt);
        ConfigureWeight<InventoryTransportReceipt>(modelBuilder, r => r.AllowanceMt!);
        ConfigureWeight<InventoryTransportReceipt>(modelBuilder, r => r.ChargeableShortageMt!);
        ConfigureMoney<InventoryTransportReceipt>(modelBuilder, r => r.FreightRateUsdPerMt!);
        ConfigureMoney<InventoryTransportReceipt>(modelBuilder, r => r.FreightCostUsd!);
        ConfigureMoney<InventoryTransportReceipt>(modelBuilder, r => r.ShortageRateUsd!);
        ConfigureMoney<InventoryTransportReceipt>(modelBuilder, r => r.ShortageChargeUsd!);
        ConfigureMoney<InventoryTransportReceipt>(modelBuilder, r => r.FreightPayableUsd!);

        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.LoadedQuantityMt);
        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.DischargedQuantityMt!);
        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.AllowanceMt!);
        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.ShortageMt!);
        ConfigureMoney<TruckDispatch>(modelBuilder, d => d.FreightCostUsd!);
        ConfigureMoney<TruckDispatch>(modelBuilder, d => d.ShortageRateUsd!);
        ConfigureMoney<TruckDispatch>(modelBuilder, d => d.FreightPayableUsd!);
        ConfigureMoney<TruckDispatch>(modelBuilder, d => d.PayableUsd!);

        // New fields on LoadingRegister (Gap #2)
        ConfigureWeight<LoadingRegister>(modelBuilder, l => l.ChargeableQuantityMt!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.RailwayRateUsd!);
        ConfigureMoney<LoadingRegister>(modelBuilder, l => l.RailwayExpenseUsd!);

        // New fields on LoadingReceipt (Gap #3)
        ConfigureWeight<LoadingReceipt>(modelBuilder, r => r.ActualArrivedQuantityMt!);
        // ضایعات معوق تا تسویهٔ مخزن: پیش‌فرض «ضایعات حالا معلوم است» تا رفتار رسیدهای قبلی حفظ شود.
        modelBuilder.Entity<LoadingReceipt>().Property(r => r.LossMode).HasDefaultValue(ReceiptLossMode.ImmediateKnownLoss);
        modelBuilder.Entity<LoadingReceipt>().HasIndex(r => r.LossMode);

        // لغو نرم رسید بارگیری: پرچم لغو ایندکس می‌شود چون تقریباً همهٔ محاسبات عملیاتی روی آن فیلتر دارند.
        modelBuilder.Entity<LoadingReceipt>().HasIndex(r => r.IsCancelled);
        modelBuilder.Entity<LoadingReceipt>().Property(r => r.RowVersion).IsRowVersion();

        // New fields on TruckDispatch (Gap #4 + #5)
        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.ToleranceMt!);
        ConfigureWeight<TruckDispatch>(modelBuilder, d => d.ChargeableShortageMt!);

        // ShipmentContract (Gap #7)
        ConfigureWeight<ShipmentContract>(modelBuilder, sc => sc.QuantityMt!);
        ConfigureWeight<ShipmentLoadingAllocation>(modelBuilder, a => a.QuantityMt);

        // CustomsDeclaration (Gap #1)
        ConfigureMoney<CustomsDeclaration>(modelBuilder, c => c.TotalAfn);
        ConfigureMoney<CustomsDeclaration>(modelBuilder, c => c.TotalUsd);
        ConfigureMoney<CustomsDeclaration>(modelBuilder, c => c.RatePerMtAfn!);
        ConfigureMoney<CustomsDeclaration>(modelBuilder, c => c.RatePerMtUsd!);
        ConfigureWeight<CustomsDeclaration>(modelBuilder, c => c.ConsignmentWeightMt!);

        // CustomsDeclarationItem (Gap #1)
        ConfigureMoney<CustomsDeclarationItem>(modelBuilder, i => i.AmountAfn);
        ConfigureMoney<CustomsDeclarationItem>(modelBuilder, i => i.AmountUsd!);

        ConfigureWeight<Shipment>(modelBuilder, s => s.QuantityMt);
        ConfigureWeight<DeliveryReceipt>(modelBuilder, r => r.ReceivedQuantityMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.ExpectedQuantityMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.ActualQuantityMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.DifferenceQuantityMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.ToleranceQuantityMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.AllowableLossMt);
        ConfigureWeight<LossEvent>(modelBuilder, e => e.ChargeableLossMt);

        ConfigureWeight<SalesTransaction>(modelBuilder, s => s.QuantityMt);
        ConfigureMoney<SalesTransaction>(modelBuilder, s => s.UnitPriceInCurrency);
        ConfigureMoney<SalesTransaction>(modelBuilder, s => s.UnitPriceUsd);
        ConfigureMoney<SalesTransaction>(modelBuilder, s => s.TotalInCurrency);
        ConfigureMoney<SalesTransaction>(modelBuilder, s => s.TotalUsd);
        modelBuilder.Entity<SalesTransaction>().Property(s => s.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SalesTransaction>().Property(s => s.Currency).HasDefaultValue("USD");

        ConfigureMoney<ExpenseRule>(modelBuilder, r => r.Amount);
        ConfigureMoney<ExpenseTransaction>(modelBuilder, e => e.Amount);
        ConfigureMoney<ExpenseTransaction>(modelBuilder, e => e.AmountUsd);
        modelBuilder.Entity<ExpenseTransaction>().Property(e => e.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<ExpenseTransaction>().Property(e => e.Currency).HasDefaultValue("USD");
        modelBuilder.Entity<ExpenseTransaction>().HasIndex(e => e.LoadingRegisterId);
        modelBuilder.Entity<ExpenseTransaction>().HasIndex(e => e.TransportLegId);
        modelBuilder.Entity<ExpenseTransaction>().HasIndex(e => e.ServiceProviderId);
        modelBuilder.Entity<ExpenseTransaction>().HasIndex(e => e.OperationalAssetId);
        modelBuilder.Entity<ExpenseTransaction>().HasIndex(e => e.DriverId);

        ConfigureMoney<PaymentTransaction>(modelBuilder, p => p.Amount);
        ConfigureMoney<PaymentTransaction>(modelBuilder, p => p.AmountUsd);
        modelBuilder.Entity<PaymentTransaction>().Property(p => p.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.ServiceProviderId);

        modelBuilder.Entity<Sarraf>().Property(s => s.IsActive).HasDefaultValue(true);

        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.RequestedAmount);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.RequestedAmountUsd);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.SarrafChargedAmount);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.SarrafChargedAmountUsd);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.SupplierAcceptedAmount);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.SupplierAcceptedAmountUsd);
        ConfigureMoney<SarrafSettlement>(modelBuilder, s => s.DifferenceAmountUsd);
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.RequestedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.SarrafRate).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.SarrafFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.SupplierAcceptedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.SupplierRate).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.Status).HasDefaultValue(SarrafSettlementStatus.Draft);
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.DifferenceTreatment).HasDefaultValue(SarrafSettlementDifferenceTreatment.AcceptedAmountOnly);
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.DifferenceType).HasDefaultValue(SarrafSettlementDifferenceType.None);
        // پیش‌فرض Out/Supplier تا رکوردهای قبلی (که این ستون‌ها را نداشتند) همان معنی قبلی را بگیرند.
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.Direction).HasDefaultValue(SarrafSettlementDirection.Out);
        modelBuilder.Entity<SarrafSettlement>().Property(s => s.CounterpartyType).HasDefaultValue(SarrafSettlementCounterpartyType.Supplier);

        ConfigureMoney<ThreeWaySettlement>(modelBuilder, s => s.CustomerPaidAmount);
        ConfigureMoney<ThreeWaySettlement>(modelBuilder, s => s.SupplierAcceptedAmount);
        ConfigureMoney<ThreeWaySettlement>(modelBuilder, s => s.CustomerPaidUsd);
        ConfigureMoney<ThreeWaySettlement>(modelBuilder, s => s.SupplierAcceptedUsd);
        ConfigureMoney<ThreeWaySettlement>(modelBuilder, s => s.DifferenceUsd);
        modelBuilder.Entity<ThreeWaySettlement>().Property(s => s.FxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<ThreeWaySettlement>().Property(s => s.CustomerPaidFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<ThreeWaySettlement>().Property(s => s.SupplierAcceptedFxRateToUsd).HasColumnType("numeric(18,6)");

        ConfigureMoney<LedgerEntry>(modelBuilder, l => l.AmountUsd);
        ConfigureMoney<LedgerEntry>(modelBuilder, l => l.SourceAmount!);
        // نرخ‌های ارز با ۱۲ رقم اعشار نگهداری می‌شوند. با ۶ رقم، 1/70 به 0.014286 گرد می‌شد و
        // معکوسش دیگر ۷۰ نبود (۶۹.۹۹۸۶). numeric(24,12) همان ۱۲ رقم صحیحِ قبلی را نگه می‌دارد،
        // پس هیچ مقدار موجودی سرریز نمی‌کند.
        modelBuilder.Entity<LedgerEntry>().Property(l => l.AppliedFxRateToUsd).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<LedgerEntry>().Property(l => l.AppliedCurrencyPerUsdRate).HasColumnType("numeric(24,12)");

        ConfigureMoney<Employee>(modelBuilder, e => e.BaseSalaryAmount);
        modelBuilder.Entity<Employee>().Property(e => e.IsActive).HasDefaultValue(true);
        ConfigureMoney<EmployeeSalaryTransaction>(modelBuilder, t => t.Amount);
        ConfigureMoney<EmployeeSalaryTransaction>(modelBuilder, t => t.AmountUsd);
        modelBuilder.Entity<EmployeeSalaryTransaction>().Property(t => t.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<EmployeeSalaryTransaction>().Property(t => t.IsCancelled).HasDefaultValue(false);

        ConfigureMoney<ContractBalanceTransfer>(modelBuilder, t => t.AmountOriginal);
        ConfigureMoney<ContractBalanceTransfer>(modelBuilder, t => t.AmountUsd);
        modelBuilder.Entity<ContractBalanceTransfer>().Property(t => t.FxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<ContractBalanceTransfer>().Property(t => t.OriginalPaymentFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<ContractBalanceTransfer>().Property(t => t.IsCancelled).HasDefaultValue(false);

        ConfigureMoney<SupplierPaymentAllocation>(modelBuilder, a => a.AllocatedPaymentAmount);
        ConfigureMoney<SupplierPaymentAllocation>(modelBuilder, a => a.AllocatedBookAmountUsd);
        ConfigureMoney<SupplierPaymentAllocation>(modelBuilder, a => a.AllocatedContractCurrencyAmount);
        ConfigureMoney<SupplierPaymentAllocation>(modelBuilder, a => a.AllocatedValueUsdAtAllocation);
        ConfigureMoney<SupplierPaymentAllocation>(modelBuilder, a => a.ExchangeDifferenceUsd);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(a => a.PaymentFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(a => a.ContractCurrencyPerUsdRate).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(a => a.ContractCurrencyFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(a => a.PaymentCurrencyPerUsdRateAtAllocation).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(a => a.PaymentCurrencyFxRateToUsdAtAllocation).HasColumnType("numeric(18,6)");

        ConfigureMoney<SupplierBalanceTransfer>(modelBuilder, t => t.TransferOriginalAmount);
        ConfigureMoney<SupplierBalanceTransfer>(modelBuilder, t => t.HistoricalAmountUsd);
        ConfigureMoney<SupplierBalanceTransfer>(modelBuilder, t => t.TransferValueUsd);
        ConfigureMoney<SupplierBalanceTransfer>(modelBuilder, t => t.ExchangeDifferenceUsd);
        ConfigureMoney<SupplierBalanceTransfer>(modelBuilder, t => t.TransferContractCurrencyAmount);
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.HistoricalFxRateToUsd).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.HistoricalCurrencyPerUsdRate).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.HistoricalRateIsEstimated).HasDefaultValue(false);
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.TransferPerUsdRate).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.TransferFxRateToUsd).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.ContractCurrencyPerUsdRate).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransfer>().Property(t => t.ContractCurrencyFxRateToUsd).HasColumnType("numeric(24,12)");
        ConfigureMoney<SupplierBalanceTransferSource>(modelBuilder, s => s.ConsumedOriginalAmount);
        ConfigureMoney<SupplierBalanceTransferSource>(modelBuilder, s => s.ConsumedBookAmountUsd);
        modelBuilder.Entity<SupplierBalanceTransferSource>().Property(s => s.HistoricalFxRateToUsd).HasColumnType("numeric(24,12)");
        modelBuilder.Entity<SupplierBalanceTransferSource>().Property(s => s.HistoricalCurrencyPerUsdRate).HasColumnType("numeric(24,12)");

        ConfigureWeight<OperationalAsset>(modelBuilder, a => a.CapacityMt!);
        ConfigureMoney<OperationalAsset>(modelBuilder, a => a.MonthlyDepreciationUsd);
        ConfigureMoney<OperationalAsset>(modelBuilder, a => a.DefaultInternalRateUsd!);
        ConfigureMoney<OperationalAsset>(modelBuilder, a => a.DefaultExternalRateUsd!);
        modelBuilder.Entity<AssetOwnershipShare>().Property(s => s.SharePercent).HasColumnType("numeric(9,4)");
        ConfigureWeight<AssetRentTransaction>(modelBuilder, r => r.QuantityMt!);
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.DistanceKm).HasColumnType("numeric(18,4)");
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.Days).HasColumnType("numeric(18,4)");
        ConfigureMoney<AssetRentTransaction>(modelBuilder, r => r.Rate);
        ConfigureMoney<AssetRentTransaction>(modelBuilder, r => r.AmountOriginal);
        ConfigureMoney<AssetRentTransaction>(modelBuilder, r => r.AmountUsd);
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.FxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.Currency).HasDefaultValue("USD");
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.IsPostedToLedger).HasDefaultValue(false);
        modelBuilder.Entity<AssetRentTransaction>().Property(r => r.IsCancelled).HasDefaultValue(false);
        modelBuilder.Entity<AssetRentShare>().Property(s => s.SharePercent).HasColumnType("numeric(9,4)");
        ConfigureMoney<AssetRentShare>(modelBuilder, s => s.ShareAmountUsd);

        ConfigureWeight<LoadingExpenseLine>(modelBuilder, l => l.QuantityMt!);
        ConfigureMoney<LoadingExpenseLine>(modelBuilder, l => l.UnitRateUsd!);
        ConfigureMoney<LoadingExpenseLine>(modelBuilder, l => l.AmountUsd);

        // ---- unique indexes ----
        modelBuilder.Entity<Product>().HasIndex(p => p.Code).IsUnique();
        modelBuilder.Entity<Currency>().HasIndex(c => c.Code).IsUnique();
        modelBuilder.Entity<Unit>().HasIndex(u => u.Code).IsUnique();
        modelBuilder.Entity<Partner>().HasIndex(p => p.Code).IsUnique();
        modelBuilder.Entity<Company>().HasIndex(c => c.Code).IsUnique();
        // فقط یک شرکت می‌تواند مالکِ سیستم باشد؛ ایندکسِ یکتای جزئی این قید را در دیتابیس تضمین می‌کند.
        modelBuilder.Entity<Company>().HasIndex(c => c.IsSystemOwner)
            .IsUnique()
            .HasFilter("\"IsSystemOwner\" = true");
        modelBuilder.Entity<ServiceProviderEntity>().HasIndex(p => p.Code);
        modelBuilder.Entity<ServiceProviderEntity>().HasIndex(p => new { p.IsActive, p.Name });
        modelBuilder.Entity<OperationalAsset>().HasIndex(a => a.AssetCode).IsUnique();
        modelBuilder.Entity<OperationalAsset>().HasIndex(a => a.AssetType);
        modelBuilder.Entity<OperationalAsset>().HasIndex(a => new { a.IsActive, a.Name });
        modelBuilder.Entity<OperationalAsset>().HasIndex(a => a.LinkedTruckId).IsUnique();
        modelBuilder.Entity<OperationalAsset>().HasIndex(a => a.LinkedStorageTankId).IsUnique();
        modelBuilder.Entity<AssetOwnershipShare>().HasIndex(s => new { s.OperationalAssetId, s.EffectiveFrom, s.EffectiveTo });
        modelBuilder.Entity<AssetOwnershipShare>().HasIndex(s => s.CompanyId);
        modelBuilder.Entity<AssetOwnershipShare>().HasIndex(s => s.PartnerId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => new { r.OperationalAssetId, r.RentDate });
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.LoadingRegisterId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.TransportLegId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.InventoryTransportReceiptId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.TruckDispatchId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ChargedToContractId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ChargedToCustomerId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ChargedToCompanyId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ChargedToPartnerId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ChargedToServiceProviderId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.LedgerEntryId);
        modelBuilder.Entity<AssetRentTransaction>().HasIndex(r => r.ReferenceDocument);
        modelBuilder.Entity<AssetRentShare>().HasIndex(s => s.AssetRentTransactionId);
        modelBuilder.Entity<AssetRentShare>().HasIndex(s => s.CompanyId);
        modelBuilder.Entity<AssetRentShare>().HasIndex(s => s.PartnerId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.LoadingRegisterId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.ExpenseTypeId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.ServiceProviderId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.OperationalAssetId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.ExpenseTransactionId);
        modelBuilder.Entity<LoadingExpenseLine>().HasIndex(l => l.AssetRentTransactionId);
        modelBuilder.Entity<Terminal>().HasIndex(t => t.Code).IsUnique();
        modelBuilder.Entity<ExpenseType>().HasIndex(e => e.Code).IsUnique();
        modelBuilder.Entity<CashAccount>().HasIndex(a => a.Code).IsUnique();
        modelBuilder.Entity<Role>().HasIndex(r => r.Name).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(e => e.EmployeeCode).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(e => e.FullName);
        modelBuilder.Entity<Employee>().HasIndex(e => e.Department);
        modelBuilder.Entity<Employee>().HasIndex(e => e.EmployeeType);
        modelBuilder.Entity<Employee>().HasIndex(e => e.SalaryCurrency);
        modelBuilder.Entity<Employee>().HasIndex(e => e.IsActive);
        modelBuilder.Entity<Truck>().HasIndex(t => t.PlateNumber).IsUnique();
        modelBuilder.Entity<Wagon>().HasIndex(w => w.WagonNumber).IsUnique();
        modelBuilder.Entity<Vessel>().HasIndex(v => v.Name).IsUnique();
        modelBuilder.Entity<Contract>().HasIndex(c => c.ContractNumber).IsUnique();
        modelBuilder.Entity<ContractPartner>().HasIndex(c => new { c.ContractId, c.PartnerId }).IsUnique();
        modelBuilder.Entity<SalesTransaction>().HasIndex(s => s.InvoiceNumber).IsUnique();
        modelBuilder.Entity<InventoryBatch>().HasIndex(b => b.BatchCode).IsUnique();
        modelBuilder.Entity<Shipment>().HasIndex(s => s.ShipmentCode).IsUnique();
        modelBuilder.Entity<InventoryMovement>().HasIndex(m => m.LoadingReceiptId).IsUnique();
        modelBuilder.Entity<InventoryMovement>().HasIndex(m => m.SalesTransactionId);
        modelBuilder.Entity<InventoryMovement>().HasIndex(m => m.ReversalOfInventoryMovementId).IsUnique();
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.LoadingReceiptId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.SourcePurchaseContractId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.InventoryMovementId).IsUnique();
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.TruckDispatchId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.SalesTransactionId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.Destination);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.Status);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.DestinationTerminalId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.DestinationStorageTankId);
        modelBuilder.Entity<LoadingReceiptAllocation>().HasIndex(a => a.DestinationLocationId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.SourcePurchaseContractId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.ShipmentId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.TransportGroupKey);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.ProductId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => new { l.SourceTerminalId, l.SourceStorageTankId });
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.Status);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.LoadedDate);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.WagonNumber);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.RwbNo);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.OutboundInventoryMovementId).IsUnique();
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.ServiceProviderId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.OperationalAssetId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.DriverId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.InventoryTransportBatchId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.TruckId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.WagonId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.FreightCurrencyId);
        modelBuilder.Entity<InventoryTransportLeg>().HasIndex(l => l.IsFreightSettled);
        modelBuilder.Entity<InventoryTransportBatch>().HasIndex(b => b.BatchNumber).IsUnique();
        modelBuilder.Entity<InventoryTransportBatch>().HasIndex(b => b.TransportGroupKey).IsUnique();
        modelBuilder.Entity<InventoryTransportBatch>().HasIndex(b => new { b.SourceTerminalId, b.SourceStorageTankId, b.ProductId });
        modelBuilder.Entity<InventoryTransportBatch>().HasIndex(b => b.Status);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.InventoryTransportLegId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.SourcePurchaseContractId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.SourceLoadingReceiptId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.SourceInventoryMovementId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.SourceTransportLegId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.SourceTransportReceiptId);
        modelBuilder.Entity<InventoryTransportLegAllocation>().HasIndex(a => a.OutboundInventoryMovementId).IsUnique();
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.InventoryTransportLegId);
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.ReceiptDate);
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.ReceiptDestination);
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.InventoryMovementId).IsUnique();
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.ServiceProviderId);
        modelBuilder.Entity<InventoryTransportReceipt>().HasIndex(r => r.OperationalAssetId);
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.LoadingReceiptAllocationId);
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.InventoryTransportReceiptId);
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.SalesTransactionId).IsUnique();
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.ServiceProviderId);
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.OperationalAssetId);
        modelBuilder.Entity<TruckDispatch>().HasIndex(d => d.IsFreightSettled);
        modelBuilder.Entity<LossEvent>().HasIndex(e => new { e.EventDate, e.Stage });
        modelBuilder.Entity<LossEvent>().HasIndex(e => e.InventoryMovementId).IsUnique();
        modelBuilder.Entity<LossEvent>().HasIndex(e => e.TransportLegId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SalesTransactionId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.TransportLegId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SourcePurchaseContractId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SourceLoadingReceiptId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SourceInventoryMovementId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SourceTransportLegId);
        modelBuilder.Entity<SalesTransactionSourceAllocation>().HasIndex(a => a.SourceTransportReceiptId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.LossEventId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.TransportLegId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.SourcePurchaseContractId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.SourceLoadingReceiptId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.SourceInventoryMovementId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.SourceTransportLegId);
        modelBuilder.Entity<LossEventSourceAllocation>().HasIndex(a => a.SourceTransportReceiptId);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.PaymentDate);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.Reference);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.LedgerEntryId).IsUnique();
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => new { p.CashAccountId, p.PaymentDate });
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.EmployeeId);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.SarrafId);
        modelBuilder.Entity<Sarraf>().HasIndex(s => s.Name);
        modelBuilder.Entity<Sarraf>().HasIndex(s => s.IsActive);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.SettlementDate);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.SarrafId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.SupplierId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.CustomerId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.ServiceProviderId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.DriverId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.EmployeeId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.ContractId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.Status);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.ReferenceNumber);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.LedgerEntryId);
        modelBuilder.Entity<SarrafSettlement>().HasIndex(s => s.ExchangeDifferenceLedgerEntryId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.SettlementDate);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.PayeeType);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.Status);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.CustomerId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.SupplierId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.SarrafId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.CustomerSaleContractId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.SupplierPurchaseContractId);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.HawalaReference);
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.CustomerLedgerEntryId).IsUnique();
        modelBuilder.Entity<ThreeWaySettlement>().HasIndex(s => s.SupplierLedgerEntryId).IsUnique();

        // Daily rates: unique per benchmark/product/date and per currency pair/date
        modelBuilder.Entity<DailyPlattsPrice>()
            .HasIndex(p => new { p.ProductId, p.BenchmarkCode, p.PriceDate })
            .IsUnique();
        modelBuilder.Entity<DailyFxRate>()
            .HasIndex(r => new { r.BaseCurrency, r.QuoteCurrency, r.RateDate })
            .IsUnique();
        modelBuilder.Entity<PlattsMonthlyManual>()
            .HasIndex(p => new { p.ProductId, p.BenchmarkCode, p.Month })
            .IsUnique();

        // Audit log lookup index
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => new { a.EntityName, a.EntityId });
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => new { a.Category, a.ActionAtUtc });
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.ActorUserId);
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.ControllerName);
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.RequestPath);
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.CorrelationId);

        // Idempotency tokens: unique token blocks duplicate submits. Explicit
        // index name so duplicate detection (FormTokenGuard) is unambiguous.
        modelBuilder.Entity<ProcessedFormToken>(b =>
        {
            b.Property(t => t.Token).HasMaxLength(64).IsRequired();
            b.Property(t => t.Purpose).HasMaxLength(128).IsRequired();
            b.Property(t => t.ReferenceType).HasMaxLength(128);
            b.HasIndex(t => t.Token).IsUnique().HasDatabaseName("IX_ProcessedFormTokens_Token");
            b.HasIndex(t => t.Purpose);
        });

        // Amendment uniqueness per contract (system rule #13).
        modelBuilder.Entity<ContractAmendment>()
            .HasIndex(a => new { a.ContractId, a.AmendmentNumber })
            .IsUnique();

        // User → Role lookup
        modelBuilder.Entity<User>()
            .HasIndex(u => u.RoleId);

        // Ledger lookup index
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => new { l.SourceType, l.SourceId });
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.Reference);
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.EntryDate);
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.SourceCurrencyCode);
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.EmployeeId);
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.ServiceProviderId);
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.DriverId);
        // جستجوی سریع گروهِ «پرداخت از طریق صراف».
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => l.ViaSarrafGroupId);
        // در هر گروه، از هر SourceType حداکثر یک ردیف مجاز است (یک Payment و یک Payable).
        // فقط برای رکوردهای گروه‌بندی‌شده اعمال می‌شود تا انبوهِ ردیف‌های Legacy با GroupId=NULL
        // (که NULLها در Postgres یکتاییِ هم را نقض نمی‌کنند) و سایر SourceTypeها آزاد بمانند.
        modelBuilder.Entity<LedgerEntry>()
            .HasIndex(l => new { l.ViaSarrafGroupId, l.SourceType })
            .IsUnique()
            .HasFilter("\"ViaSarrafGroupId\" IS NOT NULL");
        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasIndex(t => new { t.FromContractId, t.TransferDate });
        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasIndex(t => new { t.ToContractId, t.TransferDate });
        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasIndex(t => t.Reference);
        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasIndex(t => t.IsCancelled);
        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasIndex(a => new { a.PaymentTransactionId, a.Status });
        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasIndex(a => new { a.ContractId, a.Status });
        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasIndex(a => a.AllocationDate);
        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasIndex(t => new { t.SupplierId, t.Status });
        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasIndex(t => new { t.ContractId, t.Status });
        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasIndex(t => t.TransferDate);
        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasIndex(t => t.BatchId);
        modelBuilder.Entity<SupplierBalanceTransferSource>()
            .HasIndex(s => s.SupplierBalanceTransferId);
        modelBuilder.Entity<SupplierBalanceTransferSource>()
            .HasIndex(s => new { s.SourceType, s.SourceId });
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => new { t.EmployeeId, t.TransactionDate });
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => t.TransactionType);
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => new { t.SalaryPeriodYear, t.SalaryPeriodMonth });
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => t.CashAccountId);
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => t.PaymentTransactionId)
            .IsUnique();
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => t.LedgerEntryId)
            .IsUnique();
        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasIndex(t => t.IsCancelled);

        modelBuilder.Entity<LoadingReceipt>()
            .HasOne(r => r.LoadingRegister)
            .WithMany(l => l.Receipts)
            .HasForeignKey(r => r.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Unit)
            .WithMany()
            .HasForeignKey(p => p.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.SecondaryUnit)
            .WithMany()
            .HasForeignKey(p => p.SecondaryUnitId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Contract>()
            .HasOne(c => c.Unit)
            .WithMany()
            .HasForeignKey(c => c.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        // قرارداد اصلی ← زیرقراردادها (خودارجاع، یک سطح). حذف قرارداد اصلی با وجود زیرقرارداد ممنوع.
        modelBuilder.Entity<Contract>()
            .HasOne(c => c.ParentContract)
            .WithMany(c => c.ChildContracts)
            .HasForeignKey(c => c.ParentContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Contract>().HasIndex(c => c.ParentContractId);

        modelBuilder.Entity<ContractPartner>()
            .HasOne(cp => cp.Contract)
            .WithMany(c => c.ContractPartners)
            .HasForeignKey(cp => cp.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ContractPartner>()
            .HasOne(cp => cp.Partner)
            .WithMany()
            .HasForeignKey(cp => cp.PartnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceipt>()
            .HasOne(r => r.Terminal)
            .WithMany()
            .HasForeignKey(r => r.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceipt>()
            .HasOne(r => r.StorageTank)
            .WithMany()
            .HasForeignKey(r => r.StorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.LoadingReceipt)
            .WithMany(r => r.Allocations)
            .HasForeignKey(a => a.LoadingReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(a => a.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.Terminal)
            .WithMany()
            .HasForeignKey(a => a.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.StorageTank)
            .WithMany()
            .HasForeignKey(a => a.StorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.DestinationTerminal)
            .WithMany()
            .HasForeignKey(a => a.DestinationTerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.DestinationStorageTank)
            .WithMany()
            .HasForeignKey(a => a.DestinationStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.DestinationLocation)
            .WithMany()
            .HasForeignKey(a => a.DestinationLocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.InventoryMovement)
            .WithMany()
            .HasForeignKey(a => a.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.TruckDispatch)
            .WithMany()
            .HasForeignKey(a => a.TruckDispatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingReceiptAllocation>()
            .HasOne(a => a.SalesTransaction)
            .WithMany()
            .HasForeignKey(a => a.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .Property(l => l.Status)
            .HasDefaultValue(InventoryTransportLegStatus.Draft);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Shipment)
            .WithMany(s => s.InventoryTransportLegs)
            .HasForeignKey(l => l.ShipmentId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(l => l.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.SourceTerminal)
            .WithMany()
            .HasForeignKey(l => l.SourceTerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.SourceStorageTank)
            .WithMany()
            .HasForeignKey(l => l.SourceStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.DestinationTerminal)
            .WithMany()
            .HasForeignKey(l => l.DestinationTerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.DestinationStorageTank)
            .WithMany()
            .HasForeignKey(l => l.DestinationStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.DestinationLocation)
            .WithMany()
            .HasForeignKey(l => l.DestinationLocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.OutboundInventoryMovement)
            .WithMany()
            .HasForeignKey(l => l.OutboundInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.ServiceProvider)
            .WithMany()
            .HasForeignKey(l => l.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.OperationalAsset)
            .WithMany()
            .HasForeignKey(l => l.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Driver)
            .WithMany()
            .HasForeignKey(l => l.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportBatch>()
            .HasOne(b => b.SourceTerminal)
            .WithMany()
            .HasForeignKey(b => b.SourceTerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportBatch>()
            .HasOne(b => b.SourceStorageTank)
            .WithMany()
            .HasForeignKey(b => b.SourceStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportBatch>()
            .HasOne(b => b.Product)
            .WithMany()
            .HasForeignKey(b => b.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.InventoryTransportBatch)
            .WithMany(b => b.Legs)
            .HasForeignKey(l => l.InventoryTransportBatchId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Truck)
            .WithMany()
            .HasForeignKey(l => l.TruckId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Wagon)
            .WithMany()
            .HasForeignKey(l => l.WagonId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.Vessel)
            .WithMany()
            .HasForeignKey(l => l.VesselId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLeg>()
            .HasOne(l => l.FreightCurrency)
            .WithMany()
            .HasForeignKey(l => l.FreightCurrencyId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.InventoryTransportLeg)
            .WithMany(l => l.Allocations)
            .HasForeignKey(a => a.InventoryTransportLegId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(a => a.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.SourceLoadingReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceLoadingReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.SourceInventoryMovement)
            .WithMany()
            .HasForeignKey(a => a.SourceInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.OutboundInventoryMovement)
            .WithMany()
            .HasForeignKey(a => a.OutboundInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        // زنجیرهٔ حمل: مرحلهٔ والد و رسیدِ مبدأ. Restrict عمدی است — تاریخچهٔ حمل هرگز
        // نباید با حذف یک مرحله به‌صورت آبشاری پاک شود.
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.SourceTransportLeg)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportLegAllocation>()
            .HasOne(a => a.SourceTransportReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.InventoryTransportLeg)
            .WithMany()
            .HasForeignKey(r => r.InventoryTransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.DestinationTerminal)
            .WithMany()
            .HasForeignKey(r => r.DestinationTerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.DestinationStorageTank)
            .WithMany()
            .HasForeignKey(r => r.DestinationStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.InventoryMovement)
            .WithMany()
            .HasForeignKey(r => r.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.SalesTransaction)
            .WithMany()
            .HasForeignKey(r => r.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.ServiceProvider)
            .WithMany()
            .HasForeignKey(r => r.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryTransportReceipt>()
            .HasOne(r => r.OperationalAsset)
            .WithMany()
            .HasForeignKey(r => r.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TruckDispatch>()
            .Property(d => d.DispatchMode)
            .HasDefaultValue(TruckDispatchMode.FromInventory);

        modelBuilder.Entity<TruckDispatch>()
            .HasOne(d => d.LoadingReceiptAllocation)
            .WithMany(a => a.DirectTruckDispatches)
            .HasForeignKey(d => d.LoadingReceiptAllocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TruckDispatch>()
            .HasOne(d => d.InventoryTransportReceipt)
            .WithMany(r => r.DirectTruckDispatches)
            .HasForeignKey(d => d.InventoryTransportReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TruckDispatch>()
            .HasOne(d => d.SalesTransaction)
            .WithMany()
            .HasForeignKey(d => d.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TruckDispatch>()
            .HasOne(d => d.ServiceProvider)
            .WithMany()
            .HasForeignKey(d => d.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TruckDispatch>()
            .HasOne(d => d.OperationalAsset)
            .WithMany()
            .HasForeignKey(d => d.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryMovement>()
            .HasOne(m => m.LoadingReceipt)
            .WithOne(r => r.InventoryMovement)
            .HasForeignKey<InventoryMovement>(m => m.LoadingReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryMovement>()
            .HasOne(m => m.SalesTransaction)
            .WithMany()
            .HasForeignKey(m => m.SalesTransactionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<InventoryMovement>()
            .HasOne(m => m.ReversalOfInventoryMovement)
            .WithMany(m => m.Reversals)
            .HasForeignKey(m => m.ReversalOfInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureTransportOutcomeAllocations(modelBuilder);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.CashAccount)
            .WithMany()
            .HasForeignKey(p => p.CashAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Company)
            .WithMany()
            .HasForeignKey(p => p.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.CompanyId);

        modelBuilder.Entity<CashAccount>()
            .HasOne(a => a.Company)
            .WithMany()
            .HasForeignKey(a => a.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CashAccount>().HasIndex(a => a.CompanyId);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Customer)
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Supplier)
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.ServiceProvider)
            .WithMany()
            .HasForeignKey(p => p.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Sarraf)
            .WithMany(s => s.PaymentTransactions)
            .HasForeignKey(p => p.SarrafId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Driver)
            .WithMany()
            .HasForeignKey(p => p.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Employee)
            .WithMany(e => e.PaymentTransactions)
            .HasForeignKey(p => p.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Contract)
            .WithMany()
            .HasForeignKey(p => p.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.Shipment)
            .WithMany()
            .HasForeignKey(p => p.ShipmentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.SalesTransaction)
            .WithMany()
            .HasForeignKey(p => p.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.ExpenseTransaction)
            .WithMany()
            .HasForeignKey(p => p.ExpenseTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingRegister>()
            .HasOne(l => l.LogisticsServiceProvider)
            .WithMany()
            .HasForeignKey(l => l.LogisticsServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.LoadingRegister)
            .WithMany(l => l.ExpenseTransactions)
            .HasForeignKey(e => e.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.TransportLeg)
            .WithMany()
            .HasForeignKey(e => e.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.ServiceProvider)
            .WithMany()
            .HasForeignKey(e => e.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.OperationalAsset)
            .WithMany(a => a.ExpenseTransactions)
            .HasForeignKey(e => e.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.Driver)
            .WithMany()
            .HasForeignKey(e => e.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        // مصرف گروهی — سهم‌ها به رکورد اصلی ExpenseBatch وصل می‌شوند (nullable).
        modelBuilder.Entity<ExpenseTransaction>()
            .HasOne(e => e.ExpenseBatch)
            .WithMany(b => b.Expenses)
            .HasForeignKey(e => e.ExpenseBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseBatch>()
            .HasOne(b => b.ExpenseType)
            .WithMany()
            .HasForeignKey(b => b.ExpenseTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseBatch>()
            .HasOne(b => b.ServiceProvider)
            .WithMany()
            .HasForeignKey(b => b.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseBatch>()
            .HasIndex(b => b.BatchNumber);

        // فروش گروهی — ردیف‌ها به رکورد اصلی SalesBatch وصل می‌شوند (nullable).
        modelBuilder.Entity<SalesTransaction>()
            .HasOne(s => s.SalesBatch)
            .WithMany(b => b.Sales)
            .HasForeignKey(s => s.SalesBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        // قرارداد خریدِ منبع فروش (nullable) — ردیابی جواز/شرکت/بهای تمام‌شدهٔ همان قرارداد.
        modelBuilder.Entity<SalesTransaction>()
            .HasOne(s => s.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(s => s.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SalesTransaction>()
            .HasIndex(s => s.SourcePurchaseContractId);

        // موترِ منبعِ فروش مستقیم (nullable). رابطهٔ جداگانه از TruckDispatch.SalesTransactionId است:
        // آن یکی یکتاست و «اولین فروشِ موتر» را نگه می‌دارد (سازگاری عقب‌رو)، این یکی همهٔ فروش‌های
        // قسمتیِ همان موتر را به آن وصل می‌کند تا عاید در سود و زیان قرارداد/محموله قابل ردیابی بماند.
        modelBuilder.Entity<SalesTransaction>()
            .HasOne(s => s.TruckDispatch)
            .WithMany()
            .HasForeignKey(s => s.TruckDispatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SalesTransaction>()
            .HasIndex(s => s.TruckDispatchId);

        modelBuilder.Entity<SalesBatch>()
            .HasOne(b => b.Customer)
            .WithMany()
            .HasForeignKey(b => b.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureWeight<SalesBatch>(modelBuilder, b => b.TotalQuantityMt);
        ConfigureMoney<SalesBatch>(modelBuilder, b => b.UnitPriceInCurrency);
        ConfigureMoney<SalesBatch>(modelBuilder, b => b.TotalInCurrency);
        ConfigureMoney<SalesBatch>(modelBuilder, b => b.TotalUsd);
        modelBuilder.Entity<SalesBatch>().Property(b => b.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<SalesBatch>().Property(b => b.Currency).HasDefaultValue("USD");
        modelBuilder.Entity<SalesBatch>().HasIndex(b => b.BatchNumber);

        // پیش‌فروش مرحله‌ای — تحویل‌ها SalesTransaction عادی هستند و فقط با این FK به تعهد وصل می‌شوند.
        modelBuilder.Entity<SalesTransaction>()
            .HasOne(s => s.PreSaleOrder)
            .WithMany(o => o.Deliveries)
            .HasForeignKey(s => s.PreSaleOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SalesTransaction>().HasIndex(s => s.PreSaleOrderId);

        modelBuilder.Entity<PreSaleOrder>()
            .HasOne(o => o.Customer)
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PreSaleOrder>()
            .HasOne(o => o.Product)
            .WithMany()
            .HasForeignKey(o => o.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PreSaleOrder>()
            .HasOne(o => o.Company)
            .WithMany()
            .HasForeignKey(o => o.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureWeight<PreSaleOrder>(modelBuilder, o => o.QuantityMt);
        ConfigureMoney<PreSaleOrder>(modelBuilder, o => o.UnitPriceInCurrency);
        ConfigureMoney<PreSaleOrder>(modelBuilder, o => o.UnitPriceUsd);
        ConfigureMoney<PreSaleOrder>(modelBuilder, o => o.TotalInCurrency);
        ConfigureMoney<PreSaleOrder>(modelBuilder, o => o.TotalUsd);
        modelBuilder.Entity<PreSaleOrder>().Property(o => o.AppliedFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<PreSaleOrder>().Property(o => o.Currency).HasDefaultValue("USD");
        modelBuilder.Entity<PreSaleOrder>().HasIndex(o => o.OrderNumber);
        modelBuilder.Entity<PreSaleOrder>().HasIndex(o => new { o.CustomerId, o.Status });

        // تخصیص دریافت مشتری به پیش‌فروش — چند تخصیص برای یک دریافت و چند دریافت برای یک پیش‌فروش.
        modelBuilder.Entity<CustomerPaymentAllocation>()
            .HasOne(a => a.PaymentTransaction)
            .WithMany(p => p.CustomerPaymentAllocations)
            .HasForeignKey(a => a.PaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CustomerPaymentAllocation>()
            .HasOne(a => a.PreSaleOrder)
            .WithMany()
            .HasForeignKey(a => a.PreSaleOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureMoney<CustomerPaymentAllocation>(modelBuilder, a => a.AllocatedPaymentAmount);
        ConfigureMoney<CustomerPaymentAllocation>(modelBuilder, a => a.AllocatedAmountUsd);
        modelBuilder.Entity<CustomerPaymentAllocation>()
            .Property(a => a.PaymentFxRateToUsd).HasColumnType("numeric(18,6)");
        modelBuilder.Entity<CustomerPaymentAllocation>()
            .HasIndex(a => new { a.PreSaleOrderId, a.Status });
        modelBuilder.Entity<CustomerPaymentAllocation>()
            .HasIndex(a => new { a.PaymentTransactionId, a.Status });

        // مصرفِ ردیابی‌پذیرِ تخصیص روی تحویل. Restrict تا تاریخچهٔ مالی هرگز بی‌صدا حذف نشود.
        modelBuilder.Entity<CustomerPaymentAllocationApplication>()
            .HasOne(a => a.CustomerPaymentAllocation)
            .WithMany()
            .HasForeignKey(a => a.CustomerPaymentAllocationId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomerPaymentAllocationApplication>()
            .HasOne(a => a.SalesTransaction)
            .WithMany()
            .HasForeignKey(a => a.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomerPaymentAllocationApplication>()
            .HasOne(a => a.JournalEntry)
            .WithMany()
            .HasForeignKey(a => a.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureMoney<CustomerPaymentAllocationApplication>(modelBuilder, a => a.AppliedPaymentAmount);
        ConfigureMoney<CustomerPaymentAllocationApplication>(modelBuilder, a => a.AppliedAmountUsd);
        modelBuilder.Entity<CustomerPaymentAllocationApplication>()
            .HasIndex(a => new { a.SalesTransactionId, a.Status });
        modelBuilder.Entity<CustomerPaymentAllocationApplication>()
            .HasIndex(a => new { a.CustomerPaymentAllocationId, a.Status });

        // بهای واقعیِ مصرف‌شدهٔ هر pool برای برگشتِ دقیقِ COGS.
        modelBuilder.Entity<SalesCostConsumption>()
            .HasOne(c => c.SalesTransaction)
            .WithMany()
            .HasForeignKey(c => c.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureMoney<SalesCostConsumption>(modelBuilder, c => c.CostUsd);
        modelBuilder.Entity<SalesCostConsumption>()
            .Property(c => c.QuantityMt).HasColumnType("numeric(18,4)");
        modelBuilder.Entity<SalesCostConsumption>()
            .HasIndex(c => new { c.SalesTransactionId, c.Status });

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.LoadingRegister)
            .WithMany(r => r.ExpenseLines)
            .HasForeignKey(l => l.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.ExpenseType)
            .WithMany()
            .HasForeignKey(l => l.ExpenseTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.ServiceProvider)
            .WithMany()
            .HasForeignKey(l => l.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.OperationalAsset)
            .WithMany()
            .HasForeignKey(l => l.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.ExpenseTransaction)
            .WithMany()
            .HasForeignKey(l => l.ExpenseTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoadingExpenseLine>()
            .HasOne(l => l.AssetRentTransaction)
            .WithMany()
            .HasForeignKey(l => l.AssetRentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.TruckDispatch)
            .WithMany()
            .HasForeignKey(p => p.TruckDispatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.LedgerEntry)
            .WithMany()
            .HasForeignKey(p => p.LedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Sarraf)
            .WithMany(s => s.Settlements)
            .HasForeignKey(s => s.SarrafId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Supplier)
            .WithMany()
            .HasForeignKey(s => s.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Customer)
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.ServiceProvider)
            .WithMany()
            .HasForeignKey(s => s.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Driver)
            .WithMany()
            .HasForeignKey(s => s.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Employee)
            .WithMany()
            .HasForeignKey(s => s.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.Contract)
            .WithMany()
            .HasForeignKey(s => s.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.PaymentTransaction)
            .WithMany()
            .HasForeignKey(s => s.PaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.CashAccount)
            .WithMany()
            .HasForeignKey(s => s.CashAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.LedgerEntry)
            .WithMany()
            .HasForeignKey(s => s.LedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SarrafSettlement>()
            .HasOne(s => s.ExchangeDifferenceLedgerEntry)
            .WithMany()
            .HasForeignKey(s => s.ExchangeDifferenceLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // همان الگوی تسویهٔ صراف: سطر سود/زیان تسعیر تخصیص با FK قابل ردیابی است.
        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasOne(a => a.ExchangeDifferenceLedgerEntry)
            .WithMany()
            .HasForeignKey(a => a.ExchangeDifferenceLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.Customer)
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.Supplier)
            .WithMany()
            .HasForeignKey(s => s.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.Sarraf)
            .WithMany()
            .HasForeignKey(s => s.SarrafId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.CustomerSaleContract)
            .WithMany()
            .HasForeignKey(s => s.CustomerSaleContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.SupplierPurchaseContract)
            .WithMany()
            .HasForeignKey(s => s.SupplierPurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.CustomerLedgerEntry)
            .WithMany()
            .HasForeignKey(s => s.CustomerLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ThreeWaySettlement>()
            .HasOne(s => s.SupplierLedgerEntry)
            .WithMany()
            .HasForeignKey(s => s.SupplierLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LedgerEntry>()
            .HasOne(l => l.Employee)
            .WithMany(e => e.LedgerEntries)
            .HasForeignKey(l => l.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LedgerEntry>()
            .HasOne(l => l.ServiceProvider)
            .WithMany()
            .HasForeignKey(l => l.ServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LedgerEntry>()
            .HasOne(l => l.Driver)
            .WithMany()
            .HasForeignKey(l => l.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OperationalAsset>()
            .HasOne(a => a.LinkedTruck)
            .WithMany()
            .HasForeignKey(a => a.LinkedTruckId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OperationalAsset>()
            .HasOne(a => a.LinkedStorageTank)
            .WithMany()
            .HasForeignKey(a => a.LinkedStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OperationalAsset>()
            .HasOne(a => a.Location)
            .WithMany()
            .HasForeignKey(a => a.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OperationalAsset>()
            .HasOne(a => a.Terminal)
            .WithMany()
            .HasForeignKey(a => a.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetOwnershipShare>()
            .HasOne(s => s.OperationalAsset)
            .WithMany(a => a.OwnershipShares)
            .HasForeignKey(s => s.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetOwnershipShare>()
            .HasOne(s => s.Company)
            .WithMany()
            .HasForeignKey(s => s.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetOwnershipShare>()
            .HasOne(s => s.Partner)
            .WithMany()
            .HasForeignKey(s => s.PartnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.OperationalAsset)
            .WithMany(a => a.RentTransactions)
            .HasForeignKey(r => r.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.LoadingRegister)
            .WithMany(l => l.AssetRentTransactions)
            .HasForeignKey(r => r.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.TransportLeg)
            .WithMany()
            .HasForeignKey(r => r.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.InventoryTransportReceipt)
            .WithMany()
            .HasForeignKey(r => r.InventoryTransportReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.TruckDispatch)
            .WithMany()
            .HasForeignKey(r => r.TruckDispatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.ChargedToContract)
            .WithMany()
            .HasForeignKey(r => r.ChargedToContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.ChargedToCustomer)
            .WithMany()
            .HasForeignKey(r => r.ChargedToCustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.ChargedToCompany)
            .WithMany()
            .HasForeignKey(r => r.ChargedToCompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.ChargedToPartner)
            .WithMany()
            .HasForeignKey(r => r.ChargedToPartnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.ChargedToServiceProvider)
            .WithMany()
            .HasForeignKey(r => r.ChargedToServiceProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentTransaction>()
            .HasOne(r => r.LedgerEntry)
            .WithMany()
            .HasForeignKey(r => r.LedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentShare>()
            .HasOne(s => s.AssetRentTransaction)
            .WithMany(r => r.RentShares)
            .HasForeignKey(s => s.AssetRentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentShare>()
            .HasOne(s => s.Company)
            .WithMany()
            .HasForeignKey(s => s.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetRentShare>()
            .HasOne(s => s.Partner)
            .WithMany()
            .HasForeignKey(s => s.PartnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasOne(t => t.Employee)
            .WithMany(e => e.SalaryTransactions)
            .HasForeignKey(t => t.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasOne(t => t.CashAccount)
            .WithMany()
            .HasForeignKey(t => t.CashAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasOne(t => t.PaymentTransaction)
            .WithMany()
            .HasForeignKey(t => t.PaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmployeeSalaryTransaction>()
            .HasOne(t => t.LedgerEntry)
            .WithMany()
            .HasForeignKey(t => t.LedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasOne(t => t.FromContract)
            .WithMany()
            .HasForeignKey(t => t.FromContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasOne(t => t.ToContract)
            .WithMany()
            .HasForeignKey(t => t.ToContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ContractBalanceTransfer>()
            .HasOne(t => t.OriginalPaymentTransaction)
            .WithMany()
            .HasForeignKey(t => t.OriginalPaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasOne(a => a.PaymentTransaction)
            .WithMany()
            .HasForeignKey(a => a.PaymentTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasOne(a => a.Contract)
            .WithMany()
            .HasForeignKey(a => a.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasOne(t => t.Supplier)
            .WithMany()
            .HasForeignKey(t => t.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasOne(t => t.Company)
            .WithMany()
            .HasForeignKey(t => t.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasOne(t => t.Contract)
            .WithMany()
            .HasForeignKey(t => t.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        // همان الگوی تسویهٔ صراف و تخصیص پیش‌پرداخت: سطر سود/زیان نرخ ارز با FK قابل ردیابی است.
        modelBuilder.Entity<SupplierBalanceTransfer>()
            .HasOne(t => t.ExchangeDifferenceLedgerEntry)
            .WithMany()
            .HasForeignKey(t => t.ExchangeDifferenceLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // سطرهای ردیابی منبع با خودِ انتقال حذف می‌شوند؛ اثر مالی مستقلی ندارند.
        modelBuilder.Entity<SupplierBalanceTransferSource>()
            .HasOne(s => s.SupplierBalanceTransfer)
            .WithMany(t => t.Sources)
            .HasForeignKey(s => s.SupplierBalanceTransferId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LossEvent>()
            .HasOne(e => e.InventoryMovement)
            .WithOne()
            .HasForeignKey<LossEvent>(e => e.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LossEvent>()
            .HasOne(e => e.TransportLeg)
            .WithMany()
            .HasForeignKey(e => e.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);

        // Gap #7 — ShipmentContract junction
        modelBuilder.Entity<ShipmentContract>()
            .HasOne(sc => sc.Shipment)
            .WithMany(s => s.ShipmentContracts)
            .HasForeignKey(sc => sc.ShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ShipmentContract>()
            .HasOne(sc => sc.Contract)
            .WithMany()
            .HasForeignKey(sc => sc.ContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ShipmentContract>()
            .HasIndex(sc => new { sc.ShipmentId, sc.ContractId })
            .IsUnique();

        // سهم دقیق بارگیری در محموله — additive و کاملاً اختیاری.
        // حذف محموله ردیف‌های سهم را می‌برد (child واقعیِ محموله، مثل ShipmentContract)، ولی
        // قرارداد و بارگیری هرگز به‌خاطر محموله حذف نمی‌شوند (Restrict).
        modelBuilder.Entity<ShipmentLoadingAllocation>()
            .HasOne(a => a.Shipment)
            .WithMany(s => s.LoadingAllocations)
            .HasForeignKey(a => a.ShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ShipmentLoadingAllocation>()
            .HasOne(a => a.Contract)
            .WithMany()
            .HasForeignKey(a => a.ContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ShipmentLoadingAllocation>()
            .HasOne(a => a.LoadingRegister)
            .WithMany()
            .HasForeignKey(a => a.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Restrict);
        // یک بارگیری در یک محموله فقط یک ردیف دارد؛ همین، double-allocation درون‌محموله‌ای را
        // در سطح دیتابیس غیرممکن می‌کند. سقفِ بین‌محموله‌ای در سرویس اعتبارسنجی می‌شود.
        modelBuilder.Entity<ShipmentLoadingAllocation>()
            .HasIndex(a => new { a.ShipmentId, a.LoadingRegisterId })
            .IsUnique();
        modelBuilder.Entity<ShipmentLoadingAllocation>().HasIndex(a => a.LoadingRegisterId);
        modelBuilder.Entity<ShipmentLoadingAllocation>().HasIndex(a => a.ContractId);

        // Gap #1 — CustomsDeclaration relationships
        modelBuilder.Entity<CustomsDeclaration>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_CustomsDeclarations_ExactlyOneSource",
                "((CASE WHEN \"LoadingRegisterId\" IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN \"TransportLegId\" IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN \"TruckDispatchId\" IS NOT NULL THEN 1 ELSE 0 END)) = 1"));
        modelBuilder.Entity<CustomsDeclaration>()
            .HasOne(cd => cd.LoadingRegister)
            .WithMany(lr => lr.CustomsDeclarations)
            .HasForeignKey(cd => cd.LoadingRegisterId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomsDeclaration>()
            .HasOne(cd => cd.TransportLeg)
            .WithMany()
            .HasForeignKey(cd => cd.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomsDeclaration>()
            .HasOne(cd => cd.TruckDispatch)
            .WithMany()
            .HasForeignKey(cd => cd.TruckDispatchId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomsDeclarationItem>()
            .HasOne(i => i.CustomsDeclaration)
            .WithMany(cd => cd.Items)
            .HasForeignKey(i => i.CustomsDeclarationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomsDeclaration>()
            .HasIndex(cd => cd.LoadingRegisterId);
        modelBuilder.Entity<CustomsDeclaration>()
            .HasIndex(cd => cd.TransportLegId);
        modelBuilder.Entity<CustomsDeclaration>()
            .HasIndex(cd => cd.TruckDispatchId);

        modelBuilder.Entity<CustomsDeclarationDocument>()
            .HasOne(d => d.CustomsDeclaration)
            .WithMany(cd => cd.Documents)
            .HasForeignKey(d => d.CustomsDeclarationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomsDeclarationDocument>()
            .HasIndex(d => d.CustomsDeclarationId);

        ConfigureQualityInspections(modelBuilder);

        modelBuilder.ConfigureAccountingCore();
        ConfigureInventoryLineage(modelBuilder);
    }

    private static void ConfigureTransportOutcomeAllocations(ModelBuilder modelBuilder)
    {
        ConfigureWeight<SalesTransactionSourceAllocation>(modelBuilder, a => a.QuantityMt);
        ConfigureMoney<SalesTransactionSourceAllocation>(modelBuilder, a => a.AmountUsd);
        ConfigureWeight<LossEventSourceAllocation>(modelBuilder, a => a.QuantityMt);
        ConfigureMoney<LossEventSourceAllocation>(modelBuilder, a => a.ValueUsd!);

        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SalesTransaction)
            .WithMany(s => s.SourceAllocations)
            .HasForeignKey(a => a.SalesTransactionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.TransportLeg)
            .WithMany()
            .HasForeignKey(a => a.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(a => a.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SourceLoadingReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceLoadingReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SourceInventoryMovement)
            .WithMany()
            .HasForeignKey(a => a.SourceInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SourceTransportLeg)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .HasOne(a => a.SourceTransportReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesTransactionSourceAllocation>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_SalesTransactionSourceAllocations_QuantityPositive",
                "\"QuantityMt\" > 0"));

        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.LossEvent)
            .WithMany(e => e.SourceAllocations)
            .HasForeignKey(a => a.LossEventId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.TransportLeg)
            .WithMany()
            .HasForeignKey(a => a.TransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.SourcePurchaseContract)
            .WithMany()
            .HasForeignKey(a => a.SourcePurchaseContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.SourceLoadingReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceLoadingReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.SourceInventoryMovement)
            .WithMany()
            .HasForeignKey(a => a.SourceInventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.SourceTransportLeg)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportLegId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .HasOne(a => a.SourceTransportReceipt)
            .WithMany()
            .HasForeignKey(a => a.SourceTransportReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossEventSourceAllocation>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_LossEventSourceAllocations_QuantityPositive",
                "\"QuantityMt\" > 0"));
    }

    // بخش کیفیت و لابراتوار. همهٔ روابط Restrict هستند تا حذف یک قرارداد/محموله هرگز
    // سند کیفیت را بی‌سروصدا پاک نکند؛ فقط فایل‌های خودِ آزمایش Cascade می‌شوند.
    private static void ConfigureQualityInspections(ModelBuilder modelBuilder)
    {
        foreach (var (property, precision, scale) in new (string Property, int Precision, int Scale)[]
        {
            (nameof(QualityInspection.DensityKgM3), 18, 4),
            (nameof(QualityInspection.SulphurPercent), 18, 6),
            (nameof(QualityInspection.FlashPointC), 18, 4),
            (nameof(QualityInspection.WaterContentPercent), 18, 6),
            (nameof(QualityInspection.OctaneOrCetaneNumber), 18, 4)
        })
        {
            modelBuilder.Entity<QualityInspection>()
                .Property(property)
                .HasPrecision(precision, scale);
        }

        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.Company).WithMany()
            .HasForeignKey(q => q.CompanyId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.Contract).WithMany()
            .HasForeignKey(q => q.ContractId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.Shipment).WithMany()
            .HasForeignKey(q => q.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.LoadingRegister).WithMany()
            .HasForeignKey(q => q.LoadingRegisterId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.CustomsDeclaration).WithMany()
            .HasForeignKey(q => q.CustomsDeclarationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QualityInspection>()
            .HasOne(q => q.Product).WithMany()
            .HasForeignKey(q => q.ProductId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<QualityInspection>().HasIndex(q => q.ShipmentId);
        modelBuilder.Entity<QualityInspection>().HasIndex(q => q.LoadingRegisterId);
        modelBuilder.Entity<QualityInspection>().HasIndex(q => q.CustomsDeclarationId);
        modelBuilder.Entity<QualityInspection>().HasIndex(q => q.ContractId);
        modelBuilder.Entity<QualityInspection>().HasIndex(q => new { q.Status, q.SampleDate });

        modelBuilder.Entity<QualityInspectionDocument>()
            .HasOne(d => d.QualityInspection).WithMany(q => q.Documents)
            .HasForeignKey(d => d.QualityInspectionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QualityInspectionDocument>()
            .HasIndex(d => d.QualityInspectionId);
    }

    // Phase 2 — Inventory Lineage. تمام پیکربندی در یک متد جدا تا OnModelCreating شلوغ نشود.
    private static void ConfigureInventoryLineage(ModelBuilder modelBuilder)
    {
        // ---- numeric precision (system rule #1) ----
        ConfigureWeight<InventoryLot>(modelBuilder, l => l.QuantityMt);
        ConfigureWeight<InventoryLot>(modelBuilder, l => l.RemainingQuantityMt);
        ConfigureWeight<InventoryLotMovement>(modelBuilder, m => m.LoadedQuantityMt);
        ConfigureWeight<InventoryLotMovement>(modelBuilder, m => m.ReceivedQuantityMt!);
        ConfigureWeight<InventoryLotMovement>(modelBuilder, m => m.ShortageQuantityMt!);
        ConfigureWeight<SaleLotAllocation>(modelBuilder, a => a.QuantityMt);
        ConfigureMoney<SaleLotAllocation>(modelBuilder, a => a.AmountUsd!);
        ConfigureMoney<SaleLotAllocation>(modelBuilder, a => a.UnitCostUsd!);
        ConfigureWeight<LossLotAllocation>(modelBuilder, a => a.QuantityMt);
        ConfigureMoney<LossLotAllocation>(modelBuilder, a => a.ValueUsd!);
        ConfigureMoney<ExpenseLotAllocation>(modelBuilder, a => a.AmountUsd);

        // ---- InventoryLot ----
        modelBuilder.Entity<InventoryLot>().HasIndex(l => l.RootShipmentId);
        modelBuilder.Entity<InventoryLot>().HasIndex(l => l.RootContractId);
        modelBuilder.Entity<InventoryLot>().HasIndex(l => l.SupplierId);
        modelBuilder.Entity<InventoryLot>().HasIndex(l => new { l.ProductId, l.TerminalId, l.StorageTankId, l.Status });
        modelBuilder.Entity<InventoryLot>().HasIndex(l => l.ParentLotId);
        modelBuilder.Entity<InventoryLot>().HasIndex(l => new { l.SourceReferenceType, l.SourceReferenceId });
        // nullable-unique: در PostgreSQL مقادیر NULL متمایز شمرده می‌شوند، پس چند Lot بدون
        // CreatedFromMovementId مجاز است و فقط movementهای واقعی یکتا می‌مانند.
        modelBuilder.Entity<InventoryLot>().HasIndex(l => l.CreatedFromMovementId).IsUnique();

        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.Terminal).WithMany().HasForeignKey(l => l.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.StorageTank).WithMany().HasForeignKey(l => l.StorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.RootShipment).WithMany().HasForeignKey(l => l.RootShipmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.RootContract).WithMany().HasForeignKey(l => l.RootContractId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.Supplier).WithMany().HasForeignKey(l => l.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.ParentLot).WithMany(l => l.Children).HasForeignKey(l => l.ParentLotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .HasOne(l => l.CreatedFromMovement).WithMany().HasForeignKey(l => l.CreatedFromMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLot>()
            .ToTable(t =>
            {
                t.HasCheckConstraint("CK_InventoryLots_QuantityNonNegative", "\"QuantityMt\" >= 0");
                t.HasCheckConstraint("CK_InventoryLots_RemainingNonNegative", "\"RemainingQuantityMt\" >= 0");
                t.HasCheckConstraint("CK_InventoryLots_RemainingLeQuantity", "\"RemainingQuantityMt\" <= \"QuantityMt\"");
            });

        // ---- InventoryLotMovement ----
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.FromLotId);
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.ToLotId);
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.ShipmentId);
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.MovementDate);
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.MovementKind);
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => new { m.SourceReferenceType, m.SourceReferenceId });
        modelBuilder.Entity<InventoryLotMovement>().HasIndex(m => m.InventoryMovementId);

        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.FromLot).WithMany().HasForeignKey(m => m.FromLotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.ToLot).WithMany().HasForeignKey(m => m.ToLotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.FromTerminal).WithMany().HasForeignKey(m => m.FromTerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.FromStorageTank).WithMany().HasForeignKey(m => m.FromStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.ToTerminal).WithMany().HasForeignKey(m => m.ToTerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.ToStorageTank).WithMany().HasForeignKey(m => m.ToStorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.Shipment).WithMany().HasForeignKey(m => m.ShipmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryLotMovement>()
            .HasOne(m => m.InventoryMovement).WithMany().HasForeignKey(m => m.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        // ---- SaleLotAllocation ----
        modelBuilder.Entity<SaleLotAllocation>().HasIndex(a => a.SalesTransactionId);
        modelBuilder.Entity<SaleLotAllocation>().HasIndex(a => a.LotId);
        modelBuilder.Entity<SaleLotAllocation>()
            .HasOne(a => a.SalesTransaction).WithMany().HasForeignKey(a => a.SalesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleLotAllocation>()
            .HasOne(a => a.InventoryLot).WithMany().HasForeignKey(a => a.LotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleLotAllocation>()
            .ToTable(t => t.HasCheckConstraint("CK_SaleLotAllocations_QuantityNonNegative", "\"QuantityMt\" >= 0"));

        // ---- LossLotAllocation ----
        modelBuilder.Entity<LossLotAllocation>().HasIndex(a => a.LossEventId);
        modelBuilder.Entity<LossLotAllocation>().HasIndex(a => a.LotId);
        modelBuilder.Entity<LossLotAllocation>()
            .HasOne(a => a.LossEvent).WithMany().HasForeignKey(a => a.LossEventId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossLotAllocation>()
            .HasOne(a => a.InventoryLot).WithMany().HasForeignKey(a => a.LotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LossLotAllocation>()
            .ToTable(t => t.HasCheckConstraint("CK_LossLotAllocations_QuantityNonNegative", "\"QuantityMt\" >= 0"));

        // ---- ExpenseLotAllocation ----
        modelBuilder.Entity<ExpenseLotAllocation>().HasIndex(a => a.ExpenseTransactionId);
        modelBuilder.Entity<ExpenseLotAllocation>().HasIndex(a => a.LotId);
        modelBuilder.Entity<ExpenseLotAllocation>()
            .HasOne(a => a.ExpenseTransaction).WithMany().HasForeignKey(a => a.ExpenseTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExpenseLotAllocation>()
            .HasOne(a => a.InventoryLot).WithMany().HasForeignKey(a => a.LotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExpenseLotAllocation>()
            .ToTable(t => t.HasCheckConstraint("CK_ExpenseLotAllocations_AmountNonNegative", "\"AmountUsd\" >= 0"));

        ConfigureBackups(modelBuilder);
    }

    // ---- Backups -------------------------------------------------------------
    // ماژول پشتیبان‌گیری کاملاً جانبی است: هیچ رابطهٔ FK با موجودیت‌های مالی/عملیاتی ندارد
    // تا حذف یا آرشیو تاریخچهٔ بکاپ هرگز روی داده‌های کسب‌وکار اثر نگذارد.
    private static void ConfigureBackups(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupSetting>().Property(s => s.AutoBackupEnabled).HasDefaultValue(true);
        modelBuilder.Entity<BackupSetting>().Property(s => s.Schedule).HasDefaultValue(BackupSchedule.Daily);
        modelBuilder.Entity<BackupSetting>().Property(s => s.RunAtHourLocal).HasDefaultValue(2);
        modelBuilder.Entity<BackupSetting>().Property(s => s.RunAtMinuteLocal).HasDefaultValue(0);
        modelBuilder.Entity<BackupSetting>().Property(s => s.StorageTarget).HasDefaultValue(BackupStorageTarget.Server);
        modelBuilder.Entity<BackupSetting>().Property(s => s.KeepDaily).HasDefaultValue(7);
        modelBuilder.Entity<BackupSetting>().Property(s => s.KeepWeekly).HasDefaultValue(4);
        modelBuilder.Entity<BackupSetting>().Property(s => s.KeepMonthly).HasDefaultValue(6);

        modelBuilder.Entity<BackupJob>().Property(j => j.TriggerKind).HasDefaultValue(BackupTriggerKind.Manual);
        modelBuilder.Entity<BackupJob>().Property(j => j.Status).HasDefaultValue(BackupRunStatus.Running);
        modelBuilder.Entity<BackupJob>().Property(j => j.StorageTarget).HasDefaultValue(BackupStorageTarget.Server);
        modelBuilder.Entity<BackupJob>().Property(j => j.RetentionTier).HasDefaultValue(BackupRetentionTier.Daily);
        modelBuilder.Entity<BackupJob>().Property(j => j.DriveUploadStatus).HasDefaultValue(BackupDriveUploadStatus.NotRequested);
        modelBuilder.Entity<BackupJob>().HasIndex(j => j.StartedAtUtc);
        modelBuilder.Entity<BackupJob>().HasIndex(j => j.Status);
        // نام فایل کلیدِ عملیِ هر بکاپ است؛ تکراری‌شدن آن یعنی بازنویسی خاموشِ یک نسخه.
        modelBuilder.Entity<BackupJob>().HasIndex(j => j.FileName).IsUnique();
    }

    private static void ConfigureMoney<T>(ModelBuilder mb,
        System.Linq.Expressions.Expression<System.Func<T, decimal>> property) where T : class
        => mb.Entity<T>().Property(property).HasColumnType("numeric(18,4)");

    private static void ConfigureMoney<T>(ModelBuilder mb,
        System.Linq.Expressions.Expression<System.Func<T, decimal?>> property) where T : class
        => mb.Entity<T>().Property(property).HasColumnType("numeric(18,4)");

    private static void ConfigureWeight<T>(ModelBuilder mb,
        System.Linq.Expressions.Expression<System.Func<T, decimal>> property) where T : class
        => mb.Entity<T>().Property(property).HasColumnType("numeric(18,4)");

    private static void ConfigureWeight<T>(ModelBuilder mb,
        System.Linq.Expressions.Expression<System.Func<T, decimal?>> property) where T : class
        => mb.Entity<T>().Property(property).HasColumnType("numeric(18,4)");

    private void PrepareTrackedEntitiesForSave()
    {
        ApplyAuditStamps();
        NormalizeDateTimePropertiesToUtc();
    }

    private void ApplyAuditStamps()
    {
        var nowUtc = DateTime.UtcNow;
        var actorUserId = _currentUserContext?.UserId;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = nowUtc;
                entry.Entity.UpdatedAtUtc = nowUtc;
                entry.Entity.CreatedByUserId = actorUserId;
                entry.Entity.UpdatedByUserId = actorUserId;
                continue;
            }

            if (entry.State != EntityState.Modified)
                continue;

            entry.Property(e => e.CreatedAtUtc).IsModified = false;
            entry.Property(e => e.CreatedByUserId).IsModified = false;

            entry.Entity.UpdatedAtUtc = nowUtc;
            entry.Entity.UpdatedByUserId = actorUserId;
        }
    }

    private void NormalizeDateTimePropertiesToUtc()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Added and not EntityState.Modified)
                continue;

            foreach (var property in entry.Entity.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                    continue;

                var clrType = property.PropertyType;

                if (clrType == typeof(DateTime))
                {
                    var value = (DateTime)property.GetValue(entry.Entity)!;
                    property.SetValue(entry.Entity, NormalizeDateTime(value));
                    continue;
                }

                if (clrType == typeof(DateTime?) && property.GetValue(entry.Entity) is DateTime nullableValue)
                    property.SetValue(entry.Entity, NormalizeDateTime(nullableValue));
            }
        }
    }

    private static DateTime NormalizeDateTime(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
}
