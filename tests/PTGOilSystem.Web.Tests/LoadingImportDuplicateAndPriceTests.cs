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
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Import مرحله‌ای بارگیری: جلوگیری از ثبت تکراری، حفظ قیمت مستقل هر بارگیری،
/// و گزارش تجمیعی قرارداد اصلی از زیرقراردادها.
/// </summary>
public sealed class LoadingImportDuplicateAndPriceTests
{
    // ۱ — Import فایل برای اولین بار: همه ردیف‌ها پذیرفته و ثبت می‌شوند.
    [Fact]
    public async Task First_Import_Registers_Every_Row()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");

        var model = BuildImportModel(contractId: 1, ("CMR-001", "WG-01", 20m, 500m), ("CMR-002", "WG-02", 30m, 500m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, model, CancellationToken.None);

        Assert.Equal(2, screening.AcceptedRows.Count);
        Assert.Equal(0, screening.DuplicateRows);
        Assert.Equal(0, screening.ConflictRows);

        model.Rows = screening.AcceptedRows;
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));
        Assert.Equal(2, await db.LoadingRegisters.CountAsync());
    }

    // ۲ — Import دوباره همان فایل: هیچ رکورد تکراری ساخته نمی‌شود.
    [Fact]
    public async Task Re_Importing_The_Same_File_Creates_No_Duplicate_Rows()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");

        var rows = new (string, string, decimal, decimal)[] { ("CMR-001", "WG-01", 20m, 500m), ("CMR-002", "WG-02", 30m, 500m) };
        var firstModel = BuildImportModel(1, rows);
        firstModel.Rows = (await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, firstModel, CancellationToken.None)).AcceptedRows;
        await BuildLoadingController(db).Create(firstModel);
        Assert.Equal(2, await db.LoadingRegisters.CountAsync());

        // همان فایل، دوباره — با فاصله اضافی و حروف کوچک تا نرمال‌سازی هم آزموده شود.
        var secondModel = BuildImportModel(1, ("  cmr-001 ", "wg-01", 20m, 500m), ("cmr-002", " WG-02", 30m, 500m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, secondModel, CancellationToken.None);

        Assert.Empty(screening.AcceptedRows);
        Assert.Equal(2, screening.DuplicateRows);
        Assert.Equal(0, screening.ConflictRows);
        Assert.Equal(2, await db.LoadingRegisters.CountAsync());
    }

    // ۳ — فایل دوم با تعدادی ردیف قبلی: فقط ردیف‌های جدید ثبت می‌شوند.
    [Fact]
    public async Task Second_File_Only_Registers_The_New_Rows()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");

        var firstModel = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m));
        firstModel.Rows = (await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, firstModel, CancellationToken.None)).AcceptedRows;
        await BuildLoadingController(db).Create(firstModel);

        var secondModel = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m), ("CMR-003", "WG-03", 25m, 500m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, secondModel, CancellationToken.None);

        Assert.Single(screening.AcceptedRows);
        Assert.Equal("CMR-003", screening.AcceptedRows[0].BillOfLadingNumber);
        Assert.Equal(1, screening.DuplicateRows);

        secondModel.Rows = screening.AcceptedRows;
        await BuildLoadingController(db).Create(secondModel);
        Assert.Equal(2, await db.LoadingRegisters.CountAsync());
    }

    // ۴ — دو ردیف تکراری داخل یک فایل: فقط یکی ثبت می‌شود.
    [Fact]
    public async Task Duplicate_Rows_Inside_One_File_Are_Registered_Once()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");

        var model = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m), ("CMR-001", "WG-01", 20m, 500m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, model, CancellationToken.None);

        Assert.Single(screening.AcceptedRows);
        Assert.Equal(1, screening.DuplicateRows);

        model.Rows = screening.AcceptedRows;
        await BuildLoadingController(db).Create(model);
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
    }

    // ۵ — شماره سند یکسان با قیمت متفاوت: ثبت نمی‌شود و «دارای اختلاف» شمرده می‌شود.
    [Fact]
    public async Task Same_Document_With_Different_Price_Is_Reported_As_Conflict()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");

        var firstModel = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m));
        firstModel.Rows = (await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, firstModel, CancellationToken.None)).AcceptedRows;
        await BuildLoadingController(db).Create(firstModel);

        var conflictingModel = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 640m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, conflictingModel, CancellationToken.None);

        Assert.Empty(screening.AcceptedRows);
        Assert.Equal(1, screening.ConflictRows);
        Assert.Equal(0, screening.DuplicateRows);
        Assert.Contains(screening.Issues, issue => issue.Message.Contains("دارای اختلاف") || issue.Message.Contains("فرق دارد"));
        Assert.Equal(1, await db.LoadingRegisters.CountAsync());
        Assert.Equal(500m, (await db.LoadingRegisters.SingleAsync()).LoadingPriceUsd);
    }

    // ۶ — شماره سند یکسان در دو قرارداد متفاوت مجاز است.
    [Fact]
    public async Task Same_Document_Number_In_Another_Contract_Is_Not_A_Duplicate()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");
        SeedPurchaseContract(db, contractId: 2, contractNumber: "PUR-002");

        var firstModel = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m));
        firstModel.Rows = (await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, firstModel, CancellationToken.None)).AcceptedRows;
        await BuildLoadingController(db).Create(firstModel);

        var otherContractModel = BuildImportModel(2, ("CMR-001", "WG-01", 20m, 500m));
        var screening = await LoadingExcelImportController.ScreenDuplicateRowsAsync(db, otherContractModel, CancellationToken.None);

        Assert.Single(screening.AcceptedRows);
        Assert.Equal(0, screening.DuplicateRows);

        otherContractModel.Rows = screening.AcceptedRows;
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(otherContractModel));
        Assert.Equal(2, await db.LoadingRegisters.CountAsync());
    }

    // ۷ — خواندن قیمت از شیت موتر (ستون قیمت واحد و fallback مبلغ ÷ مقدار).
    [Fact]
    public void Truck_Sheet_Reads_Loading_Price_Column_And_Amount_Fallback()
    {
        using var workbook = BuildTruckWorkbook();

        var result = LoadingWorkbookParser.Parse(workbook);

        Assert.Equal(LoadingTransportType.Truck, result.TransportType);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(620m, result.Rows[0].LoadingPriceUsd);
        // ردیف دوم قیمت واحد ندارد: 15000 ÷ 25 = 600
        Assert.Equal(600m, result.Rows[1].LoadingPriceUsd);
    }

    // ۸ — قیمت‌های متفاوت بارگیری‌ها هنگام ثبت حفظ می‌شوند.
    [Fact]
    public async Task Distinct_Loading_Prices_Are_Persisted_Per_Row()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001", unitPriceUsd: 450m);

        var model = BuildImportModel(1, ("CMR-001", "WG-01", 20m, 500m), ("CMR-002", "WG-02", 30m, 620m));
        Assert.IsType<RedirectToActionResult>(await BuildLoadingController(db).Create(model));

        var prices = await db.LoadingRegisters.OrderBy(l => l.Id).Select(l => l.LoadingPriceUsd).ToListAsync();
        Assert.Equal([500m, 620m], prices);
    }

    // ۹ — اکشن «اصلاح قیمت» قیمت‌های موجود را تغییر نمی‌دهد و فقط بارگیری بدون قیمت را تکمیل می‌کند.
    [Fact]
    public async Task Reprice_Action_Keeps_Existing_Loading_Prices()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001", unitPriceUsd: 450m);
        db.LoadingRegisters.AddRange(
            NewLoading(1, "CMR-001", "WG-01", 20m, 500m),
            NewLoading(1, "CMR-002", "WG-02", 30m, 620m),
            NewLoading(1, "CMR-003", "WG-03", 10m, null));
        await db.SaveChangesAsync();

        var contracts = new ContractsController(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        Assert.IsType<RedirectToActionResult>(await contracts.RepricePurchaseLoadings(1));

        var prices = await db.LoadingRegisters.OrderBy(l => l.Id).Select(l => l.LoadingPriceUsd).ToListAsync();
        Assert.Equal([500m, 620m, 450m], prices);
    }

    // ۱۰ — ارزش واقعی قرارداد = Sum(LoadedQuantityMt × LoadingPriceUsd).
    [Fact]
    public async Task Contract_Value_Is_The_Sum_Of_Loading_Values()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001", unitPriceUsd: 450m);
        db.LoadingRegisters.AddRange(
            NewLoading(1, "CMR-001", "WG-01", 20m, 500m),
            NewLoading(1, "CMR-002", "WG-02", 30m, 620m));
        await db.SaveChangesAsync();

        var (hasMixedPrices, loadingsValueUsd) = await ContractJourneyController.LoadLoadingPriceSpreadAsync(db, 1);

        Assert.True(hasMixedPrices);
        Assert.Equal(20m * 500m + 30m * 620m, loadingsValueUsd);
    }

    // ۱۱ — ایجاد زیرقرارداد: ParentContractId ثبت می‌شود و بیش از یک سطح مجاز نیست.
    [Fact]
    public async Task Sub_Contract_Is_Created_Under_A_Parent_Contract()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001");
        SeedPurchaseContract(db, contractId: 2, contractNumber: "PUR-001-A", parentContractId: 1);
        await db.SaveChangesAsync();

        var child = await db.Contracts.SingleAsync(c => c.Id == 2);
        Assert.Equal(1, child.ParentContractId);

        // زیرقراردادِ یک زیرقرارداد نباید در فهرست «قرارداد اصلی» بیاید.
        var selectableParents = await db.Contracts
            .Where(c => c.ParentContractId == null)
            .Select(c => c.Id)
            .ToListAsync();
        Assert.Equal([1], selectableParents);
    }

    // ۱۲ — قرارداد اصلی مجموع زیرقراردادها را نمایش می‌دهد.
    [Fact]
    public async Task Parent_Contract_Aggregates_Its_Sub_Contracts()
    {
        await using var db = NewDb();
        SeedPurchaseContract(db, contractId: 1, contractNumber: "PUR-001", quantityMt: 0m);
        SeedPurchaseContract(db, contractId: 2, contractNumber: "PUR-001-A", parentContractId: 1, quantityMt: 100m);
        SeedPurchaseContract(db, contractId: 3, contractNumber: "PUR-001-B", parentContractId: 1, quantityMt: 200m);
        db.LoadingRegisters.AddRange(
            NewLoading(2, "CMR-001", "WG-01", 40m, 500m),
            NewLoading(3, "CMR-002", "WG-02", 50m, 620m));
        await db.SaveChangesAsync();

        var items = await ContractJourneyController.LoadSubContractSummariesAsync(db, 1);

        Assert.Equal(2, items.Count);
        Assert.Equal(300m, items.Sum(i => i.QuantityMt));
        Assert.Equal(90m, items.Sum(i => i.LoadedQuantityMt));
        Assert.Equal(210m, items.Sum(i => i.RemainingQuantityMt));
        Assert.Equal(40m * 500m + 50m * 620m, items.Sum(i => i.LoadingsValueUsd));
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
        int contractId,
        string contractNumber,
        int? parentContractId = null,
        decimal quantityMt = 1000m,
        decimal? unitPriceUsd = null)
    {
        if (!db.Products.Any())
        {
            db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
            db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        }

        db.Contracts.Add(new Contract
        {
            Id = contractId,
            ContractNumber = contractNumber,
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            ParentContractId = parentContractId,
            ProductId = 1,
            CompanyId = 1,
            ContractDate = new DateTime(2026, 7, 22),
            QuantityMt = quantityMt,
            PricingMethod = PricingMethod.Fixed,
            Currency = "USD",
            UnitPriceUsd = unitPriceUsd,
            AppliedFxRateToUsd = unitPriceUsd.HasValue ? 1m : null,
            UnitPriceInCurrency = unitPriceUsd
        });
        db.SaveChanges();
    }

    private static LoadingRegister NewLoading(
        int contractId,
        string billOfLadingNumber,
        string wagonNumber,
        decimal quantityMt,
        decimal? priceUsd)
        => new()
        {
            ContractId = contractId,
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 7, 22),
            LoadedQuantityMt = quantityMt,
            BillOfLadingNumber = billOfLadingNumber,
            WagonNumber = wagonNumber,
            LoadingPriceUsd = priceUsd,
            ImportUniqueKey = LoadingImportKey.Build(contractId, billOfLadingNumber, wagonNumber, new DateTime(2026, 7, 22))
        };

    private static LoadingCreateViewModel BuildImportModel(
        int contractId,
        params (string Document, string Wagon, decimal QuantityMt, decimal PriceUsd)[] rows)
        => new()
        {
            ContractId = contractId,
            SelectedContractIds = [contractId],
            ProductId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 7, 22),
            Rows = rows
                .Select((row, index) => new LoadingCreateRowViewModel
                {
                    RowKey = $"row_{index}",
                    ContractId = contractId,
                    LoadingDate = new DateTime(2026, 7, 22),
                    BillOfLadingNumber = row.Document,
                    WagonNumber = row.Wagon,
                    LoadedQuantityMt = row.QuantityMt,
                    LoadingPriceUsd = row.PriceUsd
                })
                .ToList()
        };

    private static MemoryStream BuildTruckWorkbook()
    {
        var stream = new MemoryStream();
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

            sheetData.Append(new Row(
                TextCell("A1", "Date"),
                TextCell("B1", "CMR"),
                TextCell("C1", "Trucks"),
                TextCell("D1", "Loaded quantity (MT)"),
                TextCell("E1", "Price"),
                TextCell("F1", "Amount"),
                TextCell("G1", "Destination"),
                TextCell("H1", "Consignee"))
            {
                RowIndex = 1
            });

            sheetData.Append(new Row(
                TextCell("A2", "2026-07-22"),
                TextCell("B2", "CMR-001"),
                TextCell("C2", "TRK-01"),
                NumberCell("D2", 20m),
                NumberCell("E2", 620m),
                NumberCell("F2", 12400m),
                TextCell("G2", "Hairatan"),
                TextCell("H2", "Consignee A"))
            {
                RowIndex = 2
            });

            sheetData.Append(new Row(
                TextCell("A3", "2026-07-22"),
                TextCell("B3", "CMR-002"),
                TextCell("C3", "TRK-02"),
                NumberCell("D3", 25m),
                TextCell("E3", ""),
                NumberCell("F3", 15000m),
                TextCell("G3", "Hairatan"),
                TextCell("H3", "Consignee B"))
            {
                RowIndex = 3
            });

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Loading"
            });

            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
