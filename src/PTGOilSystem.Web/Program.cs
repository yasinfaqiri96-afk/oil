using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Diagnostics;
using PTGOilSystem.Web.Infrastructure.Api;
using PTGOilSystem.Web.Infrastructure.ModelBinding;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Middleware;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.AutoCode;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.QuickCreate;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ---- Database ---------------------------------------------------------------
// Connection string is read from DATABASE_URL first, then DefaultConnection.
// DATABASE_URL is converted from the URL form
// (postgres://user:pass@host:port/db) to the Npgsql key-value form.
var rawConnectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
var hasDatabaseConnection = !string.IsNullOrWhiteSpace(rawConnectionString);

if (!hasDatabaseConnection)
{
    throw new InvalidOperationException(
        "PTG Oil System database connection is not configured. "
        + "Set DATABASE_URL or ConnectionStrings__DefaultConnection before starting the app. "
        + "The application no longer falls back to an InMemory database because that can make real PostgreSQL data look empty.");
}

builder.Services.AddScoped<MvcRequestTimingState>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<MvcQueryCountingInterceptor>();
    builder.Services.AddScoped<MvcRequestTimingFilter>();
}

builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(BuildPostgresConnectionString(rawConnectionString!), npgsql =>
    {
        // عمداً کوتاه: هدف این است که کوئری کند سریع شکست بخورد و دیده شود،
        // نه اینکه با Timeout بلند پنهان بماند و اتصال را اشغال نگه دارد.
        npgsql.CommandTimeout(30);
    });
    if (builder.Environment.IsDevelopment())
    {
        options.AddInterceptors(serviceProvider.GetRequiredService<MvcQueryCountingInterceptor>());
    }
});

// ---- Domain services (business rules, system rules #3-#9, #11, #13) --------
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IStockService, StockService>();
// تنها نقطهٔ اجرای قواعد مشترک ثبت حرکت موجودی (مقدار، قفل، نگهبان موجودی، سند).
// تراکنش را caller مالک است؛ Writer تراکنش مستقل باز نمی‌کند.
builder.Services.AddScoped<IInventoryMovementWriter, InventoryMovementWriter>();
// PTG-P1-03 — تنها مسیرِ ساختنِ LedgerEntry.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Ledger.ILedgerPostingService, PTGOilSystem.Web.Services.Ledger.LedgerPostingService>();
// تک‌منبع «باقیماندهٔ حمل» و تفکیک سرنوشت مقدار (نگهداشت مقدار).
builder.Services.AddScoped<ITransportQuantityService, TransportQuantityService>();
// موتور عمومی وسیله → وسیله (هر نُه ترکیب موتر/واگن/کشتی، بدون حرکت موجودی).
builder.Services.AddScoped<ITransportChainService, TransportChainService>();
builder.Services.AddScoped<ITransportWorkflowService, TransportWorkflowService>();
// نسب‌نامهٔ چندقراردادی نتیجه‌های حمل (فروش/کسری) بدون شکستن سند واحد و دفترکل قدیمی.
builder.Services.AddScoped<ITransportSourceAllocationService, TransportSourceAllocationService>();
// منبع واحد حقیقتِ «فروش → قرارداد خرید»: فقط SalesTransactionSourceAllocations.
builder.Services.AddScoped<ISaleContractAttributionReader, SaleContractAttributionReader>();
// صفحهٔ فقط‌خواندنیِ «مشاهده فعالیت‌های دوره». هیچ منطق مالی/نوشتنی ندارد.
builder.Services.AddScoped<IPeriodActivityService, PeriodActivityService>();
// ---- Independent accounting core (Stage 2; feature flag defaults to off). ----
builder.Services.Configure<PTGOilSystem.Web.Configuration.AccountingOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.AccountingOptions.SectionName));
builder.Services.AddScoped<IFiscalCalendarService, FiscalCalendarService>();
builder.Services.AddScoped<IPeriodGuard, PeriodGuard>();
builder.Services.AddScoped<ISystemCompanyProvider, SystemCompanyProvider>();
builder.Services.AddScoped<IAccountingPostingService, AccountingPostingService>();
builder.Services.AddScoped<IAccountingChartSeeder, AccountingChartSeeder>();
builder.Services.AddScoped<IAccountingBackfillService, AccountingBackfillService>();
builder.Services.AddScoped<IPartnershipProfitAllocationAdapter, PartnershipProfitAllocationAdapter>();
builder.Services.AddScoped<IPartnerCurrentReconciliationService, PartnerCurrentReconciliationService>();
builder.Services.AddScoped<IChartOfAccountsReadService, ChartOfAccountsReadService>();
builder.Services.AddScoped<IAccountingMappingService, AccountingMappingService>();
builder.Services.AddScoped<IAccountingJournalNumberGenerator, AccountingJournalNumberGenerator>();
builder.Services.AddScoped<IContractBalanceTransferAccountingAdapter, ContractBalanceTransferAccountingAdapter>();
builder.Services.AddScoped<ISupplierPaymentAllocationAccountingAdapter, SupplierPaymentAllocationAccountingAdapter>();
builder.Services.AddScoped<ICompanyOwnershipReportService, CompanyOwnershipReportService>();
// مرحله ۹ — گزارش فقط‌خواندنیِ آمادگی Cutover. هیچ Flag را روشن و هیچ Migration را اجرا نمی‌کند.
builder.Services.AddScoped<IAccountingReadinessService, AccountingReadinessService>();
// مرحله ۱۰ — صفحه‌های سال مالی. Overview فقط می‌خواند؛ Provisioning تنها مسیر نوشتنِ آن است.
builder.Services.AddScoped<IFiscalYearOverviewService, FiscalYearOverviewService>();
// انتخاب‌گرِ سال مالیِ فعالِ هدر — کانتکستِ کاری/گزارشی در کوکیِ امضاشده. هیچ منطقِ posting را تغییر نمی‌دهد.
builder.Services.AddScoped<IFiscalYearContext, FiscalYearContextService>();
builder.Services.AddScoped<IFiscalYearProvisioningService, FiscalYearProvisioningService>();
// مرحله ۱۲ — چک‌لیستِ بستنِ سال. کاملاً فقط‌خواندنی؛ هیچ Close/Lock/Migration/Posting ندارد.
builder.Services.AddScoped<IClosingChecklistService, ClosingChecklistService>();
// مرحله ۱۳ — Trial Close و تسعیرِ پایان دوره. سال را نمی‌بندد و دوره را HardLock نمی‌کند.
builder.Services.AddScoped<ITrialCloseService, TrialCloseService>();
// مرحله ۱۴ — Final Close اتمیک. بستنِ P&L به Equity، HardLock دوره‌ها و بستنِ سال در یک Transaction.
builder.Services.AddScoped<IFinalCloseService, FinalCloseService>();
// مرحله ۱۵ — بازگشاییِ کنترل‌شده. آثارِ Final Close فقط با Reversal رسمی برمی‌گردند؛ هیچ سندی حذف نمی‌شود.
builder.Services.AddScoped<IReopenFiscalYearService, ReopenFiscalYearService>();
// مرحله ۱۱ — تنها مسیر نوشتنِ وضعیت قفلِ دوره. قفل سخت از اینجا هم برگشت‌ناپذیر است.
builder.Services.AddScoped<IFiscalPeriodLockService, FiscalPeriodLockService>();
builder.Services.AddScoped<IPaymentCompanyResolver, PaymentCompanyResolver>();
builder.Services.AddScoped<IExpenseAccountingAdapter, ExpenseAccountingAdapter>();
builder.Services.AddScoped<IPaymentAccountingAdapter, PaymentAccountingAdapter>();
builder.Services.AddScoped<IViaSarrafAccountingAdapter, ViaSarrafAccountingAdapter>();
builder.Services.AddScoped<IInventoryValuationService, InventoryValuationService>();
builder.Services.AddScoped<IPurchaseAccountingAdapter, PurchaseAccountingAdapter>();
builder.Services.AddScoped<ISalesAccountingAdapter, SalesAccountingAdapter>();
builder.Services.AddScoped<IAssetRentAccountingAdapter, AssetRentAccountingAdapter>();
builder.Services.AddScoped<IAssetRentPostingService, AssetRentPostingService>();
builder.Services.AddScoped<IInventoryLossAccountingAdapter, InventoryLossAccountingAdapter>();
builder.Services.AddScoped<IShortageChargeAccountingAdapter, ShortageChargeAccountingAdapter>();
builder.Services.AddScoped<ISarrafSettlementAccountingAdapter, SarrafSettlementAccountingAdapter>();
builder.Services.AddScoped<IThreeWaySettlementAccountingAdapter, ThreeWaySettlementAccountingAdapter>();
builder.Services.AddScoped<IInventoryTransferAccountingAdapter, InventoryTransferAccountingAdapter>();
// ---- Inventory Lineage (Phase 2). Feature flags + parallel reference-layer services. ----
builder.Services.Configure<PTGOilSystem.Web.Configuration.LineageOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.LineageOptions.SectionName));
builder.Services.AddScoped<IInventoryLineageWriter, InventoryLineageWriter>();
builder.Services.AddScoped<InventoryLineageBackfillService>();
builder.Services.AddScoped<InventoryLineagePnlService>();
builder.Services.AddScoped<InventoryTransportLegLoadService>();
builder.Services.AddScoped<InventoryTransportBatchService>();
// از کانتینر گرفته می‌شود تا آداپترهای حسابداری/نسب‌نامه در همهٔ مسیرهای رسید حمل
// (تخلیه، فروش مستقیم، انتقال به موتر، تسویه گروهی) یکسان وصل باشند. ساختِ دستی این سرویس
// آداپترها را null می‌گذاشت و مسیرها رفتار حسابداری متفاوت پیدا می‌کردند.
builder.Services.AddScoped<InventoryTransportReceiptService>();
builder.Services.AddScoped<IPurchaseAggregationService, PurchaseAggregationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.IProfitAndLossService,
    PTGOilSystem.Web.Services.Reporting.ProfitAndLossService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reconciliation.IReconciliationService,
    PTGOilSystem.Web.Services.Reconciliation.ReconciliationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.IPreSaleReservationService,
    PTGOilSystem.Web.Services.Reporting.PreSaleReservationService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.Reporting.INegativeStockAnalysisService,
    PTGOilSystem.Web.Services.Reporting.NegativeStockAnalysisService>();
builder.Services.AddScoped<ILossEventWorkflowService, LossEventWorkflowService>();
builder.Services.AddScoped<
    PTGOilSystem.Web.Services.LoadingReceipts.ILoadingReceiptCancellationService,
    PTGOilSystem.Web.Services.LoadingReceipts.LoadingReceiptCancellationService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<ICurrencyConversionService, CurrencyConversionService>();
builder.Services.AddScoped<IUnitConversionService, UnitConversionService>();
builder.Services.AddScoped<ISarrafSettlementService, SarrafSettlementService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IExpenseRuleEngine, ExpenseRuleEngine>();
builder.Services.AddScoped<IContractAmendmentService, ContractAmendmentService>();
builder.Services.AddScoped<IContractBalanceTransferService, ContractBalanceTransferService>();
builder.Services.AddScoped<ISupplierPaymentAllocationService, SupplierPaymentAllocationService>();
// موتور واحد «مانده قابل انتقال تأمین‌کننده» — منبع اصلی انتقال طلب به قرارداد.
builder.Services.AddScoped<ISupplierTransferableBalanceService, SupplierTransferableBalanceService>();
builder.Services.AddScoped<ISupplierFxSettlementService, SupplierFxSettlementService>();
builder.Services.AddScoped<ISupplierFxRecognitionService, SupplierFxRecognitionService>();
builder.Services.AddScoped<ISupplierBalanceTransferService, SupplierBalanceTransferService>();
builder.Services.AddScoped<ICustomerPaymentAllocationService, CustomerPaymentAllocationService>();
builder.Services.AddScoped<ICustomerReceiptApplicationService, CustomerReceiptApplicationService>();
builder.Services.AddScoped<IViaSarrafContractAssignmentService, ViaSarrafContractAssignmentService>();
builder.Services.AddScoped<IViaSarrafLegacyGroupingService, ViaSarrafLegacyGroupingService>();
builder.Services.AddScoped<IPaymentCorrectionService, PaymentCorrectionService>();
builder.Services.AddScoped<IEmployeeSalaryService, EmployeeSalaryService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.Configure<PTGOilSystem.Web.Models.PartyStatements.PartyStatementOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Models.PartyStatements.PartyStatementOptions.SectionName));
// لایهٔ مرکزی «رسید، برد و بیلانس» — تنها مرجع تعیین جهت تجاری و محاسبهٔ بیلانس.
builder.Services.AddSingleton<PTGOilSystem.Web.Services.CompanyFlow.ICompanyFlowDirectionResolver,
    PTGOilSystem.Web.Services.CompanyFlow.CompanyFlowDirectionResolver>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.CompanyFlow.ICompanyFlowBalanceService,
    PTGOilSystem.Web.Services.CompanyFlow.CompanyFlowBalanceService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Expenses.IExpenseLedgerPoster,
    PTGOilSystem.Web.Services.Expenses.ExpenseLedgerPoster>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Expenses.IExpenseSettlementValidator,
    PTGOilSystem.Web.Services.Expenses.ExpenseSettlementValidator>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Parties.IPartyDirectory,
    PTGOilSystem.Web.Services.Parties.PartyDirectory>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.PartyStatements.IPartyStatementPolicyResolver,
    PTGOilSystem.Web.Services.PartyStatements.PartyStatementPolicyResolver>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartyStatementReadService,
    PTGOilSystem.Web.Services.PartyStatements.PartyStatementReadService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.PartyStatementPageBuilder>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartnershipStatementService,
    PTGOilSystem.Web.Services.PartyStatements.PartnershipStatementService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.PartyStatements.IPartyBalanceReadService,
    PTGOilSystem.Web.Services.PartyStatements.PartyBalanceReadService>();
builder.Services.AddScoped<ILoginAttemptGuard, AuditLoginAttemptGuard>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Time.IAfghanistanBusinessClock,
    PTGOilSystem.Web.Services.Time.AfghanistanBusinessClock>();
builder.Services.AddScoped<IAutoCodeService, AutoCodeService>();
builder.Services.AddScoped<AutoGeneratedCodeFilter>();
builder.Services.AddScoped<QuickCreateResultFilter>();
builder.Services.AddScoped<MasterDataDeleteSafetyService>();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();
builder.Services.AddScoped<IFormTokenGuard, FormTokenGuard>();
// PTG-P1-01 — قفل دورهٔ عملیاتی. مستقل از ماژول حسابداری (که خاموش است) و تنها مرجعِ
// «آیا در این تاریخ می‌شود سند زد؟» برای دفترِ عملیاتی.
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.IOperationalPeriodGuard,
    PTGOilSystem.Web.Services.OperationalPeriod.OperationalPeriodGuard>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.BusinessRuleExceptionFilter>();
// PTG فاز ۹ — درخواستِ صریح و یک‌بارمصرفِ «ثبت استثنایی در دورهٔ بسته».
builder.Services.AddScoped<PTGOilSystem.Web.Services.OperationalPeriod.ClosedPeriodOverrideFilter>();
// PTG-P1-02 / ۱۲-F — تطبیق فقط‌خواندنیِ سلامتِ دفتر کل و دادهٔ تاریخی.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Reconciliation.ILedgerIntegrityReconciliationService,
    PTGOilSystem.Web.Services.Reconciliation.LedgerIntegrityReconciliationService>();
builder.Services.AddScoped<RoleNavigationAuthorizationFilter>();
builder.Services.AddScoped<AuthBootstrapper>();
builder.Services.AddHttpContextAccessor();

// ---- راهنمای هوشمند --------------------------------------------------------
// کلید API فقط از متغیر محیطی (GROQ_API_KEY / ANTHROPIC_API_KEY) خوانده می‌شود و
// هرگز به Frontend نمی‌رود. دستیار می‌تواند داده را فقط بخواند: هیچ ابزار نوشتنی
// ثبت نشده و هر ابزار پیش از اجرا با دسترسی ناوبری همان کاربر سنجیده می‌شود.
builder.Services.Configure<PTGOilSystem.Web.Configuration.AssistantOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.AssistantOptions.SectionName));
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.AssistantPageCatalog>();
// هر سه Provider ثبت می‌شوند؛ انتخاب واقعی با Assistant:Provider و
// Assistant:FallbackProvider انجام می‌شود.
builder.Services.AddHttpClient(PTGOilSystem.Web.Services.Assistant.GroqAssistantProvider.HttpClientName);
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.GeminiAssistantProvider>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.AnthropicAssistantProvider>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Assistant.IAssistantProvider,
    PTGOilSystem.Web.Services.Assistant.GroqAssistantProvider>();

// ابزارهای فقط‌خواندنی. Scoped چون به DbContext و سرویس‌های خواندنیِ Scoped تکیه دارند.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.SearchPartyTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.PartyBalanceTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.StockBalanceTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.ContractLookupTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.LoadingDetailsTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.ContractProgressTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.OpenContractsTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantTool,
    PTGOilSystem.Web.Services.Assistant.Tools.PartyLedgerTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.Tools.IAssistantToolRegistry,
    PTGOilSystem.Web.Services.Assistant.Tools.AssistantToolRegistry>();

// Scoped است چون ابزارهایش به DbContext وابسته‌اند؛ Singleton بودن آن باعث
// نگه‌داشتن یک DbContext در طول عمر برنامه می‌شد.
builder.Services.AddScoped<PTGOilSystem.Web.Services.Assistant.IAssistantService,
    PTGOilSystem.Web.Services.Assistant.AssistantService>();

// ---- Backups (پشتیبان‌گیری کامل؛ خارج از منطق مالی/عملیاتی) -----------------
// اجرای واقعی فقط در BackupSchedulerHostedService رخ می‌دهد و BackupExecutionLock
// تک‌نمونه‌ای است تا دو بکاپ هرگز هم‌زمان نشوند.
builder.Services.Configure<PTGOilSystem.Web.Configuration.BackupOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Configuration.BackupOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Backups.IBackupExecutionLock,
    PTGOilSystem.Web.Services.Backups.BackupExecutionLock>();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Backups.IBackupTriggerQueue,
    PTGOilSystem.Web.Services.Backups.BackupTriggerQueue>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IPostgresBackupTool,
    PTGOilSystem.Web.Services.Backups.PostgresBackupTool>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IGoogleDriveBackupClient,
    PTGOilSystem.Web.Services.Backups.GoogleDriveBackupClient>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IBackupService,
    PTGOilSystem.Web.Services.Backups.BackupService>();
builder.Services.AddScoped<PTGOilSystem.Web.Services.Backups.IBackupRestoreService,
    PTGOilSystem.Web.Services.Backups.BackupRestoreService>();
builder.Services.AddHostedService<PTGOilSystem.Web.Services.Backups.BackupSchedulerHostedService>();
// ۱۲-A — پاک‌سازیِ کران‌دارِ توکن‌های Idempotency. توکنِ داخلِ پنجرهٔ تلاش دوباره حذف نمی‌شود.
builder.Services.AddHostedService<PTGOilSystem.Web.Services.ProcessedFormTokenRetentionHostedService>();

var configuredDataProtectionKeysPath = builder.Configuration["PTG_DATA_PROTECTION_KEYS_PATH"];
var shouldUseLocalDataProtectionKeys =
    !string.IsNullOrWhiteSpace(configuredDataProtectionKeysPath)
    || builder.Environment.IsDevelopment()
    || OperatingSystem.IsWindows();

if (shouldUseLocalDataProtectionKeys)
{
    var dataProtectionKeysPath = string.IsNullOrWhiteSpace(configuredDataProtectionKeysPath)
        ? Path.Combine(builder.Environment.ContentRootPath, ".aspnet-data-protection-keys")
        : configuredDataProtectionKeysPath;

    Directory.CreateDirectory(dataProtectionKeysPath);

    builder.Services
        .AddDataProtection()
        .SetApplicationName("PTGOilSystem.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "text/css", "application/javascript", "text/javascript",
        "application/json", "text/html", "image/svg+xml",
        "font/woff2", "font/woff", "font/ttf", "font/otf",
        "application/font-woff2", "application/font-woff"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Imports.ExcelImportJobCoordinator>();
builder.Services.Configure<PTGOilSystem.Web.Services.Exports.TabularExportOptions>(
    builder.Configuration.GetSection(PTGOilSystem.Web.Services.Exports.TabularExportOptions.SectionName));
builder.Services.AddSingleton<PTGOilSystem.Web.Services.Exports.ITabularExportService, PTGOilSystem.Web.Services.Exports.TabularExportService>();

// ---- Rate limiting (مسیرهای سنگین) -----------------------------------------
// هدف: جلوگیری از تمام‌شدن Connection Pool وقتی کاربر روی «خروجی CSV» یا گزارش
// سنگین پی‌درپی کلیک می‌کند. تفکیک بر اساس کاربر واردشده، و در نبود آن بر اساس IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Login, httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: $"login-ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 1,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // سهمیهٔ خروجی به تفکیک «مسیر» است، همان چیزی که پیام رد شدن هم می‌گوید. قبلاً همهٔ
    // ۴۵ اکشن خروجی برنامه (CSV/Excel/PDF) یک سطل ۵تایی مشترک برای هر کاربر داشتند؛
    // بنابراین چند خروجی معمولی در یک دقیقه سهمیه را تمام می‌کرد و کلیک بعدی — مثلاً
    // PDF صورت‌حساب تأمین‌کننده — با 429 رد می‌شد بی‌آنکه خودش پرمصرف بوده باشد.
    // کلید از الگوی مسیر ساخته می‌شود نه از URL کامل، تا id‌های مختلف سطل جدید نسازند.
    // سقف ۵ برای کار عادی کم بود: Excel و PDF یک مسیر مشترک دارند (format فقط query است)
    // و چند خروجی پشت‌سرهم از یک صفحه، کاربر را با 429 روبه‌رو می‌کرد. سقف ۶۰ در دقیقه
    // همچنان جلوی سیل درخواست و تمام‌شدن Connection Pool را می‌گیرد.
    options.AddPolicy(RateLimitPolicies.CsvExport, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{ResolveRateLimitPartitionKey(httpContext)}|{ResolveRateLimitRouteKey(httpContext)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.HeavyReport, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        var isLoginRequest = context.HttpContext.Request.Path.StartsWithSegments("/Auth/Login")
            || context.HttpContext.Request.Path.StartsWithSegments("/api/mobile/v1/auth/login");
        if (isLoginRequest)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store, no-cache";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            context.HttpContext.Response.Headers.XFrameOptions = "DENY";

            try
            {
                await using var scope = context.HttpContext.RequestServices.CreateAsyncScope();
                var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
                await audit.LogActivityAndSaveAsync(new AuditLogEntryInput
                {
                    Category = PTGOilSystem.Web.Models.Entities.AuditLogCategories.Authentication,
                    EntityName = "User",
                    Action = LoginAuditActions.RateLimited,
                    Module = "Auth",
                    Description = "درخواست ورود به دلیل محدودیت نرخ رد شد.",
                    HttpMethod = context.HttpContext.Request.Method,
                    RequestPath = context.HttpContext.Request.Path.Value,
                    ControllerName = "Auth",
                    ActionName = "Login",
                    StatusCode = StatusCodes.Status429TooManyRequests,
                    IsSuccess = false,
                    CorrelationId = context.HttpContext.TraceIdentifier,
                    IpAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = context.HttpContext.Request.Headers.UserAgent.ToString()
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("LoginRateLimiting");
                logger.LogWarning(ex, "Unable to persist login rate-limit audit event.");
            }
        }

        // مهلت واقعی را اعلام کن تا کاربر/مرورگر نداند «کمی» یعنی چقدر.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // API موبایل پاسخ ProblemDetails می‌گیرد؛ متن ساده فقط برای صفحات وب می‌ماند.
        if (ApiRequest.IsApi(context.HttpContext))
        {
            await ApiProblem.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                ApiErrorCodes.RateLimited,
                ApiProblem.RateLimitedMessage);
            return;
        }

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            isLoginRequest
                ? "ورود موقتاً محدود شده است. لطفاً کمی بعد دوباره تلاش کنید."
                : "تعداد درخواست‌های شما برای این مسیر بیش از حد مجاز است. لطفاً کمی صبر کنید و دوباره تلاش کنید.",
            cancellationToken);
    };
});

// ---- Authentication / Authorization -----------------------------------------
// Behind Replit's reverse proxy the app receives plain HTTP; trust the
// X-Forwarded-Proto header so the framework knows the original request was
// HTTPS. This makes the Secure cookie policy below behave correctly.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Proxy IP is not fixed on Replit autoscale; clear the known-proxy allow-list.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// کوکی وب (همان تنظیمات قبلی، طرح پیش‌فرض) + JWT Bearer فقط برای /api/mobile.
// تنظیمات کامل و سیاست‌ها: Security/AuthenticationSetup.cs
builder.Services.AddPtgAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPtgMobileApi();

// ---- MVC --------------------------------------------------------------------
builder.Services.AddControllersWithViews(options =>
{
    // فرم انتقال گروهی می‌تواند هزاران ردیف داشته باشد؛ محدودیت قدیمی ۱۰۰ ردیف حذف شده است.
    options.MaxModelBindingCollectionSize = 20_000;
    // تاریخ‌های query/form با Kind=Unspecified بایند می‌شوند و Npgsql آن‌ها را برای
    // ستون timestamptz رد می‌کند؛ این provider همه را به UTC نرمال می‌کند.
    options.ModelBinderProviders.Insert(0, new UtcDateTimeModelBinderProvider());
    // فرم‌های ثبت جدید دیگر صفرِ پیش‌فرض را در input نمی‌گذارند؛ پس فیلد عددیِ اختیاری می‌تواند
    // خالی post شود. این provider همان خالی را به صفر bind می‌کند و [Required] را دست نمی‌زند.
    options.ModelBinderProviders.Insert(1, new OptionalNumericModelBinderProvider());
    // ?page=0 قرارداد داخلیِ Export است، نه چیزی که کاربر از نوار آدرس بدهد؛ این فیلتر
    // فقط مقدار آمده از خودِ درخواست را به بازهٔ مجاز برمی‌گرداند.
    options.Filters.Add<PTGOilSystem.Web.Filters.ListPageBoundsFilter>();
    options.Filters.AddService<AutoGeneratedCodeFilter>();
    options.Filters.AddService<RoleNavigationAuthorizationFilter>();
    options.Filters.AddService<QuickCreateResultFilter>();
    // PTG-P1-01 / P1-05 / P3-C — قفل دوره، برخورد هم‌زمانی و خطای محدودیت دیتابیس باید
    // پیام فارسی بدهند، نه صفحهٔ «خطای سرور» با متن فنی.
    // PTG فاز ۹ — پیش از اجرای Action خوانده می‌شود تا اگر کاربرِ مجاز دلیل نوشته باشد،
    // عبورِ همین یک درخواست باز شود. بدون دسترسی یا بدون دلیل، هیچ اثری ندارد.
    options.Filters.AddService<PTGOilSystem.Web.Services.OperationalPeriod.ClosedPeriodOverrideFilter>();
    options.Filters.AddService<PTGOilSystem.Web.Services.OperationalPeriod.BusinessRuleExceptionFilter>();
    if (builder.Environment.IsDevelopment())
    {
        options.Filters.AddService<MvcRequestTimingFilter>();
    }
});

// نکته: Razor Runtime Compilation عمداً فعال نشد. کامپایلر runtime نسخهٔ زبان C# 12 را
// رعایت نمی‌کند و collection expression مثل `[]` را رد می‌کند (Invalid expression term '[').
// برای دیدن زندهٔ تغییرات .cshtml بدون restart از dotnet watch استفاده کنید (run-dev.bat).

var app = builder.Build();

// ---- بررسی پیکربندی دستیار ---------------------------------------------------
// فقط گزارش می‌کند و هرگز برنامه را متوقف نمی‌کند: نبودن کلید دستیار نباید جلوی
// بالا آمدن سامانه را بگیرد. هیچ مقدار کلیدی Log نمی‌شود، فقط نام متغیر محیطی.
PTGOilSystem.Web.Services.Assistant.AssistantStartupValidator.Validate(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Assistant"));

// ---- Auto-migrate database --------------------------------------------------
// Migrations run on startup by default to preserve existing server behaviour.
// Set PTG_AUTO_MIGRATE=false (or ConnectionStrings/Database:AutoMigrate=false)
// to disable — the desktop wrapper sets this so it never migrates implicitly.
var autoMigrate = builder.Configuration.GetValue("PTG_AUTO_MIGRATE", true)
    && builder.Configuration.GetValue("Database:AutoMigrate", true);
if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    DatabaseSafetyGuard.EnsureMigrationAllowed(db.Database.GetDbConnection().Database);
    db.Database.Migrate();
}

// ---- Accounting backfill (CLI) ----------------------------------------------
// `dotnet run -- --accounting-backfill [--dry-run]` replays the purchase, inventory-receipt,
// sale and COGS postings for data entered while the accounting module was switched off, then
// exits without serving. It creates no operational data; every artifact comes from the same
// production adapter the create-time path uses, so a second run posts nothing new.
if (args.Contains("--accounting-backfill", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunAccountingBackfillAsync(app, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// ---- Loading price repair (CLI) ---------------------------------------------
// `dotnet run -- --repair-loading-prices [--dry-run]` تعمیرِ یک‌بارهٔ دادهٔ قدیمی است: بارگیری‌های
// خریدِ بدون قیمت معتبر که قراردادشان نرخ نهاییِ قطعی دارد، با همان نرخ تکمیل می‌شوند. هیچ منطق
// قیمت‌گذاری تازه‌ای اینجا نیست؛ کار را همان مسیر موجودِ «اصلاح قیمت» قرارداد
// (ContractsController.RepricePurchaseLoadings → SyncPurchaseLoadingPricesAsync) انجام می‌دهد، پس
// قفل روبل، دفتر تأمین‌کننده و اسناد حسابداری هم مثل فشردن همان دکمه هماهنگ می‌شوند. بارگیریِ
// قیمت‌دار و قراردادِ بدون نرخ قطعی دست‌نخورده می‌مانند.
if (args.Contains("--repair-loading-prices", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunLoadingPriceRepairAsync(app, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

// `dotnet run -- --partner-reconciliation` prints, per partnership contract and partner, what the
// partnership statement says, what the ledger's partner current account holds, and the named
// reason for any difference. Read-only.
if (args.Contains("--partner-reconciliation", StringComparer.OrdinalIgnoreCase))
{
    await using var reconciliationScope = app.Services.CreateAsyncScope();
    var reconciliation = reconciliationScope.ServiceProvider
        .GetRequiredService<IPartnerCurrentReconciliationService>();
    var report = await reconciliation.BuildAsync();

    Console.WriteLine("== Partner current reconciliation ==");
    foreach (var row in report.Rows)
    {
        Console.WriteLine(
            $"{row.ContractNumber} / {row.PartnerName}: statement={row.StatementNetPositionUsd:N2}, "
            + $"ledger={row.LedgerPartnerCurrentUsd:N2}, companyFunded={row.CompanyFundedContributionUsd:N2}, "
            + $"partnerFunded={row.PartnerFundedContributionUsd:N2}, proceedsHeld={row.SaleProceedsHeldUsd:N2}, "
            + $"profitShare={row.ProfitShareUsd:N2}, unsoldCostShare={row.UnsoldCostShareUsd:N2}, "
            + $"difference={row.DifferenceUsd:N2} [{row.DifferenceReason ?? "none"}]");
    }

    Console.WriteLine($"Fully reconciled: {report.IsFullyReconciled}");
    return report.IsFullyReconciled ? 0 : 1;
}

// `dotnet run -- --partnership-profit-discrepancies` lists, per partnership contract, the profit
// already appropriated in the ledger against the contract's current realized profit. Read-only:
// nothing is posted, reversed or rewritten. A changed contract needs a human decision (reverse the
// old allocation, then run --accounting-backfill, which is idempotent and supports --dry-run).
if (args.Contains("--partnership-profit-discrepancies", StringComparer.OrdinalIgnoreCase))
{
    await using var discrepancyScope = app.Services.CreateAsyncScope();
    var allocation = discrepancyScope.ServiceProvider
        .GetRequiredService<IPartnershipProfitAllocationAdapter>();
    var rows = await allocation.FindDiscrepanciesAsync();

    Console.WriteLine("== Partnership profit allocation vs realized contract profit ==");
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.ContractNumber}: posted={(row.PostedProfitUsd?.ToString("N2") ?? "none")}, "
            + $"realized={row.CanonicalProfitUsd:N2}, difference={row.DifferenceUsd:N2} [{row.Status}]");
    }

    var changed = rows.Count(x => x.Status == PartnershipProfitAllocationDiscrepancy.ProfitChanged);
    Console.WriteLine($"Changed since allocation: {changed}");
    return changed == 0 ? 0 : 1;
}

await SeedAuthenticationAsync(app);

// Must run first so downstream middleware sees the real scheme/host from the proxy.
app.UseForwardedHeaders();

// HTML responses get Vary: X-PTG-SPA (and no-store for SPA-stripped renders) so
// the browser HTTP cache never serves a stripped page to a normal navigation.
app.UseMiddleware<SpaCacheHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
// Note: HTTPS termination is handled by Replit's reverse proxy.
// UseHsts / UseHttpsRedirection are intentionally omitted to avoid
// redirect loops when the app receives plain HTTP from the proxy.
app.UseResponseCompression();

// Health-check probe — must respond 200 before auth middleware runs
// so that Replit's autoscale startup probe succeeds.
app.MapGet("/health", () => Results.Ok("healthy")).AllowAnonymous();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path;

        if (path.StartsWithSegments("/css")
            || path.StartsWithSegments("/js")
            || path.StartsWithSegments("/vendor")
            || path.StartsWithSegments("/images")
            || path.StartsWithSegments("/img")
            || path.StartsWithSegments("/assets")
            || path.StartsWithSegments("/favicon.ico"))
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
        else if (path.StartsWithSegments("/uploads"))
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=86400";
        }
    }
});

// فقط /api: خطا و پاسخ خالیِ 401/403/404 به ProblemDetails؛ صفحات وب دست نمی‌خورند.
app.UseMiddleware<ApiErrorMiddleware>();
app.UseRouting();
app.UseAuthentication();
// پس از UseAuthentication تا تفکیک بر اساس کاربر واردشده انجام شود، نه فقط IP.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAutoSignInMiddleware>();
}

app.UseAuthorization();
app.UseMiddleware<ActivityLogMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
return 0;

// ---- Helpers ----------------------------------------------------------------
static async Task<int> RunAccountingBackfillAsync(WebApplication app, bool dryRun)
{
    using var scope = app.Services.CreateScope();
    var backfill = scope.ServiceProvider.GetRequiredService<IAccountingBackfillService>();
    var report = await backfill.RunAsync(dryRun);

    Console.WriteLine(dryRun ? "== Accounting backfill - DRY RUN ==" : "== Accounting backfill ==");
    Console.WriteLine($"Chart of accounts seeded: {report.ChartSeeded}");
    foreach (var step in report.Steps)
    {
        Console.WriteLine(
            $"{step.Name}: candidates={step.Candidates}, "
            + $"{(dryRun ? "to post" : "posted")}={step.Posted}, "
            + $"skipped existing={step.SkippedExisting}, skipped other={step.SkippedOther}"
            + (step.Reasons.Count > 0 ? $" [{string.Join(", ", step.Reasons)}]" : string.Empty));
    }
    foreach (var error in report.Errors)
        Console.WriteLine($"ERROR: {error}");
    Console.WriteLine($"Errors: {report.Errors.Count}");

    return report.Succeeded ? 0 : 1;
}

static async Task<int> RunLoadingPriceRepairAsync(WebApplication app, bool dryRun)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    // «بدون قیمت معتبر» با همان تعریف IsPricePending و همان فیلتر SyncPurchaseLoadingPricesAsync.
    var pricelessByContract = await db.LoadingRegisters
        .AsNoTracking()
        .Where(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m)
        .GroupBy(l => l.ContractId)
        .Select(g => new { ContractId = g.Key, Count = g.Count() })
        .ToListAsync();

    var contractIds = pricelessByContract.Select(row => row.ContractId).ToList();
    var purchaseContracts = contractIds.Count == 0
        ? []
        : await db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .ToListAsync();

    // «نرخ نهاییِ قطعی» با همان تعریف سیستم؛ اینجا چیزی محاسبه نمی‌شود.
    var repairableContracts = purchaseContracts
        .Where(c => ContractPricingAdapter.GetCanonicalFinalPrice(c).HasValue)
        .OrderBy(c => c.ContractNumber, StringComparer.Ordinal)
        .ToList();
    var repairableContractIds = repairableContracts.Select(c => c.Id).ToHashSet();

    var pendingByContractId = pricelessByContract.ToDictionary(row => row.ContractId, row => row.Count);
    var totalPending = pendingByContractId.Values.Sum();
    var repairablePending = pendingByContractId
        .Where(row => repairableContractIds.Contains(row.Key))
        .Sum(row => row.Value);

    Console.WriteLine(dryRun ? "== Loading price repair - DRY RUN ==" : "== Loading price repair ==");
    Console.WriteLine("-- Before --");
    Console.WriteLine($"Loadings without a valid price: {totalPending}");
    Console.WriteLine($"  repairable (contract has a final price): {repairablePending} in {repairableContracts.Count} contract(s)");
    Console.WriteLine($"  left pending (contract has no final price): {totalPending - repairablePending}");

    if (repairableContracts.Count == 0)
    {
        Console.WriteLine("Nothing to repair.");
        return 0;
    }

    foreach (var contract in repairableContracts)
    {
        var finalPriceUsd = ContractPricingAdapter.GetCanonicalFinalPrice(contract)!.Value;
        Console.WriteLine(
            $"{contract.ContractNumber}: {pendingByContractId[contract.Id]} priceless loading(s) "
            + $"{(dryRun ? "would take" : "take")} {finalPriceUsd:N4} USD/MT");
    }

    if (dryRun)
    {
        Console.WriteLine("Dry run - no change was written.");
        return 0;
    }

    var repaired = 0;
    var errors = 0;
    foreach (var contract in repairableContracts)
    {
        // همان اکشنی که کاربر در برنامه می‌زند: قیمت‌های موجود را دست نمی‌زند و فقط
        // بارگیری بدون قیمت را تکمیل می‌کند، سپس دفتر و حسابداری را هماهنگ می‌کند.
        await using var contractScope = app.Services.CreateAsyncScope();
        var controller = ActivatorUtilities.CreateInstance<ContractsController>(contractScope.ServiceProvider);
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), new MaintenanceTempDataProvider());

        try
        {
            await controller.RepricePurchaseLoadings(contract.Id);
            repaired += pendingByContractId[contract.Id];
        }
        catch (Exception ex)
        {
            errors++;
            Console.WriteLine($"ERROR {contract.ContractNumber}: {ex.Message}");
        }
    }

    await using var verifyScope = app.Services.CreateAsyncScope();
    var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var remaining = await verifyDb.LoadingRegisters
        .AsNoTracking()
        .CountAsync(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m);

    Console.WriteLine("-- After --");
    Console.WriteLine($"Repaired loadings: {repaired}");
    Console.WriteLine($"Loadings still without a price: {remaining}");
    Console.WriteLine($"Errors: {errors}");

    return errors == 0 ? 0 : 1;
}

// کلید تفکیک محدودسازی نرخ: کاربر واردشده، و در نبود آن IP مبدأ.
static string ResolveRateLimitPartitionKey(HttpContext httpContext)
    => httpContext.User?.Identity?.IsAuthenticated == true
        ? $"user:{httpContext.User.Identity.Name}"
        : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

// الگوی مسیر (نه URL) تا هر اکشن خروجی سطل خودش را داشته باشد و تعداد سطل‌ها با
// id‌های مختلف رشد نکند.
static string ResolveRateLimitRouteKey(HttpContext httpContext)
    => (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
        ?? httpContext.Request.Path.Value
        ?? "unknown";

static async Task SeedAuthenticationAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var bootstrapper = scope.ServiceProvider.GetRequiredService<AuthBootstrapper>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    await bootstrapper.EnsureSeedDataAsync(new BootstrapAdminOptions
    {
        Username = configuration["PTG_BOOTSTRAP_ADMIN_USERNAME"] ?? "admin",
        FullName = configuration["PTG_BOOTSTRAP_ADMIN_FULLNAME"] ?? "System Administrator",
        Password = configuration["PTG_BOOTSTRAP_ADMIN_PASSWORD"]
    });
}

static string BuildPostgresConnectionString(string raw)
{
    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Username={username};Password={password};Database={database};{DbTlsSettings()}";
    }
    return raw;
}

// TLS hardening is opt-in via PTG_DB_STRICT_TLS=true so the default Replit/Postgres
// connection (self-signed cert) keeps working unless strict TLS is explicitly enabled.
static string DbTlsSettings()
{
    var strict = string.Equals(
        Environment.GetEnvironmentVariable("PTG_DB_STRICT_TLS"),
        "true",
        StringComparison.OrdinalIgnoreCase);
    return strict
        ? "SSL Mode=Require;Trust Server Certificate=false"
        : "SSL Mode=Prefer;Trust Server Certificate=true";
}

// نگهدارندهٔ بی‌اثر TempData برای اجرای اکشن قرارداد از خط فرمان (بیرون از چرخهٔ درخواست).
sealed class MaintenanceTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context)
        => new Dictionary<string, object?>();

    public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
    {
    }
}
