using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قطعی‌شدن قیمت بارگیری در همان لحظهٔ ثبت/ایمپورت: بارگیریِ بدون قیمت، نرخ نهاییِ قطعی
/// قرارداد را می‌گیرد و «در انتظار نرخ» نمی‌ماند؛ قیمت مستقل هر بارگیری دست‌نخورده می‌ماند و
/// قراردادِ بدون نرخ قطعی همچنان بارگیری را Pending نگه می‌دارد.
/// </summary>
public sealed class LoadingContractFinalPriceStampingTests
{
    // ۱ — قرارداد نرخ نهایی دارد و سطر قیمت ندارد: قیمت قرارداد ذخیره می‌شود.
    [Fact]
    public async Task Priceless_Row_Takes_The_Contract_Final_Price()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);

        var model = BuildModel(("CMR-001", "WG-01", 20m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(450m, loading.LoadingPriceUsd);
    }

    // ۲ — سطری که قیمت خودش را دارد هرگز با نرخ قرارداد بازنویسی نمی‌شود.
    [Fact]
    public async Task Explicit_Row_Price_Is_Never_Overwritten_By_The_Contract_Rate()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);

        var model = BuildModel(
            ("CMR-001", "WG-01", 20m, 500m),
            ("CMR-002", "WG-02", 30m, 620m),
            ("CMR-003", "WG-03", 10m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var prices = await db.LoadingRegisters
            .OrderBy(l => l.Id)
            .Select(l => l.LoadingPriceUsd)
            .ToListAsync();

        // قیمت‌های دستی حفظ؛ فقط سطر بی‌قیمت با نرخ قرارداد تکمیل می‌شود.
        Assert.Equal([500m, 620m, 450m], prices);
    }

    // ۳ — قرارداد Platts بدون نرخ نهایی دستی: قیمت قطعی ندارد، پس سطر Pending می‌ماند.
    [Fact]
    public async Task Contract_Without_A_Final_Price_Leaves_The_Row_Pending()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.FormulaPlatts, plattsManualPriceUsd: 638.06m);

        var model = BuildModel(("CMR-001", "WG-01", 20m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Null(loading.LoadingPriceUsd);
    }

    // ۴ — قرارداد Platts با نرخ نهایی دستی: همان نرخ نهایی روی سطر بی‌قیمت می‌نشیند.
    [Fact]
    public async Task Platts_Contract_With_Manual_Final_Price_Stamps_That_Price()
    {
        await using var db = NewDb();
        SeedPurchaseContract(
            db,
            PricingMethod.FormulaPlatts,
            plattsManualPriceUsd: 638.06m,
            manualFinalPriceUsd: 468.06m);

        var model = BuildModel(("CMR-001", "WG-01", 20m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(468.06m, loading.LoadingPriceUsd);
    }

    // ۵ — نرخ نهایی دستی بر قیمت ثابت قرارداد مقدم است (همان قاعدهٔ GetCanonicalFinalPrice).
    [Fact]
    public async Task Manual_Final_Price_Wins_Over_The_Fixed_Unit_Price()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m, manualFinalPriceUsd: 512m);

        var model = BuildModel(("CMR-001", "WG-01", 20m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var loading = await db.LoadingRegisters.SingleAsync();
        Assert.Equal(512m, loading.LoadingPriceUsd);
    }

    // ۶ — ایمپورت فایل بدون ستون قیمت (همان ساختار فایل‌های MASHAL): سطرهای پیش‌نمایش
    // همان‌جا نرخ قطعی قرارداد را می‌گیرند، پس بعد از ثبت هم «نرخ معطل» نمی‌مانند.
    [Fact]
    public async Task Excel_Import_Without_A_Price_Column_Uses_The_Contract_Final_Price()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);

        var controller = BuildLoadingController(db);
        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1],
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildPricelessTruckWorkbookBytes())
        });

        var payload = Assert.IsType<LoadingImportResponse>(Assert.IsType<JsonResult>(result).Value);
        Assert.True(payload.Success);
        Assert.Equal(2, payload.Rows.Count);
        Assert.All(payload.Rows, row => Assert.Equal(450m, row.Row.LoadingPriceUsd));
    }

    // ۷ — ایمپورت فایل دارای ستون قیمت: قیمت خود فایل حفظ می‌شود، نه نرخ قرارداد.
    [Fact]
    public async Task Excel_Import_With_A_Price_Column_Keeps_The_File_Price()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);

        var controller = BuildLoadingController(db);
        var result = await controller.ImportWorkbook(new LoadingCreateViewModel
        {
            ContractId = 1,
            SelectedContractIds = [1],
            ProductId = 1,
            ImportWorkbookFile = BuildWorkbookFile(BuildPricedTruckWorkbookBytes())
        });

        var payload = Assert.IsType<LoadingImportResponse>(Assert.IsType<JsonResult>(result).Value);
        Assert.True(payload.Success);
        Assert.Equal(2, payload.Rows.Count);
        Assert.Equal(620m, payload.Rows[0].Row.LoadingPriceUsd);
        // ردیف دوم قیمت واحد ندارد: مبلغ ÷ مقدار = 15000 ÷ 25 = 600 (نه نرخ قرارداد).
        Assert.Equal(600m, payload.Rows[1].Row.LoadingPriceUsd);
    }

    // ۸ — لیست بارگیری و چرخه قرارداد برای یک بارگیری نتیجهٔ متناقض نمی‌دهند:
    // مقدار ذخیره‌شده همان نرخ قرارداد است، پس fallback نمایشی چرخه چیزی را پنهان نمی‌کند.
    [Fact]
    public async Task Stored_Price_Matches_What_The_Journey_Fallback_Would_Show()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);

        var model = BuildModel(("CMR-001", "WG-01", 20m, null));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var loading = await db.LoadingRegisters.SingleAsync();
        var contract = await db.Contracts.AsNoTracking().SingleAsync();
        var journeyFallback = await new PricingService(db).CalculateContractPriceAsync(contract);

        Assert.False(loading.LoadingPriceUsd is null or <= 0m);
        Assert.Equal(journeyFallback.FinalUnitPrice, loading.LoadingPriceUsd);
    }

    // ۹ — داده‌های قدیمی: «اصلاح قیمت» قرارداد فقط بارگیری بدون قیمت را تکمیل می‌کند.
    [Fact]
    public async Task Contract_Reprice_Still_Only_Fills_Priceless_Loadings()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, PricingMethod.Fixed, unitPriceUsd: 450m);
        db.LoadingRegisters.AddRange(
            NewLoading("CMR-001", "WG-01", 20m, 500m),
            NewLoading("CMR-002", "WG-02", 30m, null));
        await db.SaveChangesAsync();

        var contracts = new ContractsController(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        Assert.IsType<RedirectToActionResult>(await contracts.RepricePurchaseLoadings(1));

        var prices = await db.LoadingRegisters
            .OrderBy(l => l.Id)
            .Select(l => l.LoadingPriceUsd)
            .ToListAsync();

        Assert.Equal([500m, 450m], prices);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingController BuildLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private static void SeedPurchaseContract(
        ApplicationDbContext db,
        PricingMethod pricingMethod,
        decimal? unitPriceUsd = null,
        decimal? plattsManualPriceUsd = null,
        decimal? manualFinalPriceUsd = null)
    {
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 7, 22),
            QuantityMt = 1000m,
            PricingMethod = pricingMethod,
            Currency = "USD",
            UnitPriceUsd = unitPriceUsd,
            AppliedFxRateToUsd = unitPriceUsd.HasValue ? 1m : null,
            UnitPriceInCurrency = unitPriceUsd,
            PlattsPeriodType = pricingMethod == PricingMethod.FormulaPlatts ? PlattsPeriodType.Manual : null,
            PlattsManualPriceUsd = plattsManualPriceUsd,
            ManualFinalPriceUsd = manualFinalPriceUsd
        });
        db.SaveChanges();
    }

    private static LoadingCreateViewModel BuildModel(
        params (string Document, string Wagon, decimal QuantityMt, decimal? PriceUsd)[] rows)
        => new()
        {
            ContractId = 1,
            SelectedContractIds = [1],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 7, 22),
            Rows = rows
                .Select((row, index) => new LoadingCreateRowViewModel
                {
                    RowKey = $"row_{index}",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 7, 22),
                    BillOfLadingNumber = row.Document,
                    WagonNumber = row.Wagon,
                    LoadedQuantityMt = row.QuantityMt,
                    LoadingPriceUsd = row.PriceUsd
                })
                .ToList()
        };

    private static LoadingRegister NewLoading(
        string billOfLadingNumber,
        string wagonNumber,
        decimal quantityMt,
        decimal? priceUsd)
        => new()
        {
            ContractId = 1,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 7, 22),
            LoadedQuantityMt = quantityMt,
            BillOfLadingNumber = billOfLadingNumber,
            WagonNumber = wagonNumber,
            LoadingPriceUsd = priceUsd
        };

    private static IFormFile BuildWorkbookFile(byte[] bytes)
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "ImportWorkbookFile", "loading.xlsx");

    // فایل بدون هیچ ستون قیمتی — همان ستون‌های فایل‌های واقعی MASHAL.
    private static byte[] BuildPricelessTruckWorkbookBytes()
        => BuildTruckWorkbookBytes(withPriceColumns: false);

    private static byte[] BuildPricedTruckWorkbookBytes()
        => BuildTruckWorkbookBytes(withPriceColumns: true);

    private static byte[] BuildTruckWorkbookBytes(bool withPriceColumns)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            static Cell TextCell(string reference, string value) => new()
            {
                CellReference = reference,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value))
            };

            static Cell NumberCell(string reference, decimal value) => new()
            {
                CellReference = reference,
                CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
            };

            var header = new Row
            {
                RowIndex = 1
            };
            header.Append(
                TextCell("A1", "Date"),
                TextCell("B1", "CMR"),
                TextCell("C1", "Trucks"),
                TextCell("D1", "Loaded quantity (MT)"),
                TextCell("E1", "Consignee"),
                TextCell("F1", "Destination"));
            if (withPriceColumns)
            {
                header.Append(TextCell("G1", "Price"), TextCell("H1", "Amount"));
            }

            sheetData.Append(header);

            var first = new Row { RowIndex = 2 };
            first.Append(
                TextCell("A2", "2026-07-22"),
                TextCell("B2", "CMR-001"),
                TextCell("C2", "TRK-01"),
                NumberCell("D2", 20m),
                TextCell("E2", "Consignee A"),
                TextCell("F2", "Hairatan"));
            if (withPriceColumns)
            {
                first.Append(NumberCell("G2", 620m), NumberCell("H2", 12400m));
            }

            sheetData.Append(first);

            var second = new Row { RowIndex = 3 };
            second.Append(
                TextCell("A3", "2026-07-22"),
                TextCell("B3", "CMR-002"),
                TextCell("C3", "TRK-02"),
                NumberCell("D3", 25m),
                TextCell("E3", "Consignee B"),
                TextCell("F3", "Hairatan"));
            if (withPriceColumns)
            {
                second.Append(TextCell("G3", ""), NumberCell("H3", 15000m));
            }

            sheetData.Append(second);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Loading"
            });

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
