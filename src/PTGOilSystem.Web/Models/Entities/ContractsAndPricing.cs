using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Entities;

public enum ContractType { Purchase = 1, Sale = 2 }
public enum PricingMethod { Fixed = 1, FormulaPlatts = 2, ManualFinalPrice = 3 }
public enum PlattsPeriodType { Daily = 1, Monthly = 2, Manual = 3 }
public enum ContractStatus { Draft = 0, Active = 1, Closed = 2, Cancelled = 3 }
public enum ContractOwnershipType { Personal = 1, Partnership = 2 }
public enum RubSettlementRatePolicy { NotApplicable = 0, FixedContractRate = 1, PerLoadingRate = 2, RateLater = 3 }

public class Contract : BaseEntity, IVersionedEntity, ICanonicalSearchable
{
    public bool IsArchived { get; set; }
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    [Required, MaxLength(200)] public string ContractName { get; set; } = "";
    [Required, MaxLength(50)] public string ContractNumber { get; set; } = "";

    [NotMapped]
    public string DisplayLabel => BuildDisplayLabel(ContractName, ContractNumber);

    public static string BuildDisplayLabel(string? contractName, string? contractNumber)
    {
        var name = contractName?.Trim();
        var number = contractNumber?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, number, StringComparison.Ordinal)
            ? number
            : $"{name} — {number}";
    }

    public ContractType ContractType { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    // قرارداد اصلی. خالی یعنی قرارداد مستقل یا خودش قرارداد اصلی است؛ مقدارداشتن یعنی زیرقرارداد.
    // فقط یک سطح مجاز است: قراردادی که خودش ParentContractId دارد نمی‌تواند والد دیگری باشد.
    public int? ParentContractId { get; set; }
    public Contract? ParentContract { get; set; }
    public ICollection<Contract> ChildContracts { get; set; } = [];

    // ردیفِ قرارداد خرید چندتأمین‌کننده. خالی یعنی قرارداد عادی.
    // خودِ ردیف یک قرارداد کامل است؛ بارگیری، رسید، پرداخت و دفتر کل روی همین قرارداد می‌مانند.
    public int? PurchaseContractGroupId { get; set; }
    public PurchaseContractGroup? PurchaseContractGroup { get; set; }

    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>
    /// قرارداد خرید با چند تأمین‌کننده. خالی یعنی قرارداد عادی با <see cref="SupplierId"/>.
    /// وقتی پر است، <see cref="SupplierId"/> خالی می‌ماند و هیچ‌کدام «اصلی» نیست. بارگیری با
    /// <see cref="LoadingRegister.SupplierId"/> کامل به همان تأمین‌کننده می‌رسد؛ بارگیری بدون
    /// تأمین‌کننده به نسبت ارزش خرید هر ردیف (<see cref="ContractSupplier.PurchaseValueUsd"/>) تقسیم می‌شود.
    /// </summary>
    public ICollection<ContractSupplier> ContractSuppliers { get; set; } = [];
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    public int? PurchaseSourceLocationId { get; set; }
    public Location? PurchaseSourceLocation { get; set; }
    public int? DestinationStorageTankId { get; set; }
    public StorageTank? DestinationStorageTank { get; set; }
    public ContractOwnershipType OwnershipType { get; set; } = ContractOwnershipType.Personal;
    public ICollection<ContractPartner> ContractPartners { get; set; } = [];

    /// <summary>
    /// شریکی که عایدِ فروشِ این قرارداد نزد او مانده است. فقط برای قرارداد شراکتی معنا دارد.
    /// این فیلد هیچ فروش یا رسید جدیدی نمی‌سازد و Revenue را تغییر نمی‌دهد؛ فقط مشخص می‌کند
    /// پولِ حاصل از همان فروشِ ثبت‌شده دستِ کدام شریک است تا صورت‌حساب شراکت درست شود.
    /// </summary>
    public int? SaleProceedsHolderPartnerId { get; set; }
    public Partner? SaleProceedsHolderPartner { get; set; }

    public DateTime ContractDate { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public PricingMethod PricingMethod { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? UnitPriceInCurrency { get; set; }
    public decimal? AppliedFxRateToUsd { get; set; }
    public decimal? UnitPriceUsd { get; set; }   // for Fixed
    public decimal? PremiumUsd { get; set; }     // for FormulaPlatts (+/-)
    [MaxLength(100)] public string? BenchmarkCode { get; set; }
    public PlattsPeriodType? PlattsPeriodType { get; set; }
    public decimal? PremiumDiscountUsd { get; set; }
    public decimal? PlattsManualPriceUsd { get; set; }
    public DateTime? PlattsBasisDate { get; set; }
    public DateTime? PlattsBasisMonth { get; set; }
    [MaxLength(50)] public string Currency { get; set; } = "USD";
    public decimal? MinimumPriceUsd { get; set; }
    public decimal? ManualFinalPriceUsd { get; set; }
    [MaxLength(500)] public string? PricingFormulaNote { get; set; }

    [MaxLength(10)] public string SettlementCurrencyCode { get; set; } = "USD";
    public RubSettlementRatePolicy RubRatePolicy { get; set; } = RubSettlementRatePolicy.NotApplicable;
    public decimal? ContractRubPerUsdRate { get; set; }
    public DateTime? ContractRubRateDate { get; set; }
    [MaxLength(200)] public string? ContractRubRateSource { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { ContractName, ContractNumber });
}

/// <summary>
/// سرِبرگِ قرارداد خرید چندتأمین‌کننده. عمداً <see cref="Contract"/> نیست: هیچ بارگیری، پرداخت،
/// موجودی یا سند مالی نمی‌تواند روی سرِبرگ ثبت شود و مقدار آن در گزارش‌ها دوباره جمع نمی‌شود.
/// هر ردیف یک <see cref="Contract"/> با تأمین‌کننده، مقدار و نرخ خودش است.
/// </summary>
public class PurchaseContractGroup : BaseEntity
{
    [Required, MaxLength(50)] public string GroupNumber { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    public DateTime ContractDate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public ICollection<Contract> Lines { get; set; } = [];
}

/// <summary>یکی از تأمین‌کننده‌های قرارداد خرید چندتأمین‌کننده (یک قرارداد، یک کد).</summary>
public class ContractSupplier : BaseEntity
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>مقدار خرید از این تأمین‌کننده (تُن). خالی فقط در رکوردهای پیش از ثبت سهم.</summary>
    public decimal? QuantityMt { get; set; }

    /// <summary>قیمت خرید فی تُن از این تأمین‌کننده (USD).</summary>
    public decimal? UnitPriceUsd { get; set; }

    /// <summary>ارزش خرید این تأمین‌کننده = مقدار × قیمت؛ مبنای سهم او از طلب هر بارگیری.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public decimal? PurchaseValueUsd => QuantityMt is > 0m && UnitPriceUsd is > 0m
        ? decimal.Round(QuantityMt.Value * UnitPriceUsd.Value, 4, MidpointRounding.AwayFromZero)
        : null;
}

/// <summary>
/// سهمِ یک شریک در یک قرارداد شراکتی، در یک بازهٔ زمانی مشخص.
///
/// PTG-P0-03 — این جدول عمداً «تاریخ‌دار» است. پیش از این فقط یک سطر زنده به‌ازای هر شریک
/// وجود داشت و همهٔ گزارش‌ها درصدِ امروز را روی رویدادهای پارسال هم اعمال می‌کردند؛ نتیجه این
/// بود که تغییر ۵۰/۵۰ به ۸۰/۲۰ سهمِ مفادِ دوره‌های بستهٔ گذشته را هم بازنویسی می‌کرد
/// (۱۶۲٬۰۰۰ USD جابه‌جایی بدون هیچ رویداد مالی).
///
/// الگو دقیقاً همان <see cref="AssetOwnershipShare"/> است که از قبل در همین سیستم وجود دارد:
/// هر تغییر، بازهٔ جاری را می‌بندد و یک بازهٔ تازه باز می‌کند. رکوردهای تاریخی حذف نمی‌شوند.
/// </summary>
public class ContractPartner : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public decimal SharePercent { get; set; }

    /// <summary>آغاز اعتبار این سهم (شامل همین تاریخ).</summary>
    public DateTime EffectiveFrom { get; set; } = AfghanistanBusinessClock.SystemToday;

    /// <summary>
    /// پایان اعتبار (این تاریخ دیگر شامل نیست). خالی یعنی بازهٔ جاری و باز.
    /// </summary>
    public DateTime? EffectiveTo { get; set; }
}

public class ContractAmendment : BaseEntity
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public DateTime AmendmentDate { get; set; }
    [Required, MaxLength(50)] public string AmendmentNumber { get; set; } = "";
    [Required, MaxLength(2000)] public string ChangeSummary { get; set; } = "";
    public decimal? NewQuantityMt { get; set; }
    public decimal? NewUnitPriceUsd { get; set; }
    public decimal? NewPremiumUsd { get; set; }
}

public class ContractPricingRule : BaseEntity
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public PricingMethod Method { get; set; }
    [MaxLength(100)] public string? PlattsBenchmarkCode { get; set; }
    public decimal? PremiumUsd { get; set; }
    [MaxLength(50)] public string? FxBaseCurrency { get; set; }
    [MaxLength(50)] public string? FxQuoteCurrency { get; set; }
}

public class DailyPlattsPrice : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Required, MaxLength(100)] public string BenchmarkCode { get; set; } = "";
    public DateTime PriceDate { get; set; }
    public decimal PriceUsdPerMt { get; set; }
    [MaxLength(500)] public string? Source { get; set; }
}

public class DailyFxRate : BaseEntity
{
    [Required, MaxLength(10)] public string BaseCurrency { get; set; } = "USD";
    [Required, MaxLength(10)] public string QuoteCurrency { get; set; } = "AFN";
    public DateTime RateDate { get; set; }
    public decimal Rate { get; set; }
    [MaxLength(500)] public string? Source { get; set; }
}

public class PlattsMonthlyManual : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Required, MaxLength(100)] public string BenchmarkCode { get; set; } = "";
    public DateTime Month { get; set; }
    public decimal PriceUsdPerMt { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}
