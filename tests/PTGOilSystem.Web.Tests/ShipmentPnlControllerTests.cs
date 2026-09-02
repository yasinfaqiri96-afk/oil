using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.ShipmentPnl;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ShipmentPnlControllerTests
{
    [Theory]
    [InlineData("summary", 4)]
    [InlineData("flow", 6)]
    // ستون «نمبر وسیله» به هر سه تب اضافه شده است.
    [InlineData("compliance", 6)]
    [InlineData("balance", 6)]
    [InlineData("sales", 8)]
    public void Details_Tab_Export_Builds_A_Dedicated_Document(
        string tab,
        int expectedColumnCount)
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            Id = 17,
            ShipmentCode = "SHIP-17",
            ContractNumber = "CTR-17",
            ProductName = "Diesel",
            OriginalShipmentQuantityMt = 100m
        };

        var document = ShipmentPnlController.BuildDetailsTabExportDocument(model, tab, isEnglish: true);

        // سربرگ خروجی فقط نام محموله است؛ نام تب در نام فایل می‌ماند و خط فیلترها حذف شده.
        Assert.Equal("Shipment SHIP-17", document.TitleEn);
        Assert.Contains(tab, document.FileNameStem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(document.Filters);
        Assert.Equal(expectedColumnCount, document.Columns.Count);
        Assert.Equal(tab, ShipmentPnlController.NormalizeDetailsExportTab(tab));
    }

    [Fact]
    public void Details_Tab_Export_Normalizes_Unknown_Tabs_To_Summary_And_Keeps_Finance()
    {
        Assert.Equal("summary", ShipmentPnlController.NormalizeDetailsExportTab("unknown"));
        Assert.Equal("finance", ShipmentPnlController.NormalizeDetailsExportTab(" FINANCE "));
    }

    [Fact]
    public void Details_View_Uses_Vessel_Case_File_Sections_And_Trip_Actions()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/ShipmentPnlController.cs");

        Assert.Contains("ak-form-page", view);
        Assert.Contains("_AkPageHeader.cshtml", view);
        Assert.Contains("_DetailsTabs.cshtml", view);
        Assert.Contains("Context.Request.Query[\"tab\"]", view);
        Assert.DoesNotContain("data-shipment-file-tabs", view);
        Assert.Contains("data-instant-tabs", view);
        Assert.Contains("#shipment-summary|", view);
        // تب «موجودی و انتقالات» از پروندهٔ محموله حذف شده است.
        Assert.DoesNotContain("#shipment-trips|", view);
        Assert.DoesNotContain("tab-pane fade", view);
        Assert.Contains("tab-content ak-detail-content ak-tab-content", view);
        // شش شبکهٔ کارتِ مخصوصِ هر تب با نمای خطی مشترک بازنشسته شدند (commit 1550151)؛
        // قاعدهٔ آن حالا در Shipment_Removes_The_Per_Tab_Kpi_Card_Dashboard است.
        // آنچه اینجا هنوز pin می‌شود ترتیب صفحه است: اعداد محموله، بعد ریل تب، بعد محتوا.
        var overviewPosition = view.IndexOf("_DetailOverview.cshtml", StringComparison.Ordinal);
        var activityPosition = view.IndexOf("_DetailActivityList.cshtml", StringComparison.Ordinal);
        var tabsPosition = view.IndexOf("_DetailsTabs.cshtml", StringComparison.Ordinal);
        var tabContentPosition = view.IndexOf("tab-content ak-detail-content ak-tab-content", StringComparison.Ordinal);
        Assert.True(overviewPosition >= 0, "Shipment key indicators must be rendered.");
        Assert.True(activityPosition > overviewPosition, "The activity list must follow the headline metrics.");
        Assert.True(tabsPosition > activityPosition, "Shipment tabs must follow the shipment numbers.");
        Assert.True(tabContentPosition > tabsPosition, "Shipment tab content must follow the tab rail.");
        // یک منوی خروجی مشترک که با تب فعال عوض می‌شود — نه یکی به‌ازای هر تب.
        Assert.Single(Regex.Matches(view, "data-ak-tab=\"shipment-"));
        Assert.Empty(Regex.Matches(view, "class=\"ak-stat-grid mb-3\" data-ak-tab="));
        Assert.Single(Regex.Matches(view, "new PTGOilSystem.Web.Services.Exports.ExportMenuModel\\("));
        Assert.Contains("[\"tab\"] = exportTab", view);
        Assert.Contains("ak-summary", view);
        Assert.Contains("بار داخل کشتی", view);
        Assert.Contains("باقی‌مانده", view);
        Assert.Contains("کل مصارف", view);
        Assert.Contains("موجودی در مخزن", view);
        Assert.Contains("ردیابی قطعی", controller);
        Assert.Contains("داده تخمینی", controller);
        Assert.Contains("داده قدیمی", controller);
        Assert.Contains("نیازمند بازبینی", controller);
        Assert.Contains("بر اساس اتصال‌های فعلی سیستم", view);
        Assert.Contains("خلاصه", view);
        Assert.DoesNotContain("موجودی و انتقالات", view);
        Assert.Contains("مصارف و گمرک", view);
        Assert.Contains("کسری‌ها", view);
        Assert.Contains("فروش‌ها", view);
        Assert.Contains("سود و زیان", view);
        Assert.DoesNotContain("سفره‌های موتر/واگن", view);
        // طراحی تأییدشده: نوار عملیات گروهی disabled و چک‌باکس‌های انتخاب سفر حذف شدند.
        Assert.DoesNotContain("data-shipment-batch-bar", view);
        Assert.DoesNotContain("data-shipment-trip-select", view);
        Assert.Contains("dropdown-menu", view);
        Assert.Contains("ثبت مصرف", view);
        Assert.Contains("ثبت فروش", view);
        Assert.Contains("ثبت کسری", view);
        Assert.Contains("@(canRegisterDirectLoss ? null : \"disabled\")", view);
        Assert.Contains("ثبت تخلیه", view);
        Assert.Contains("asp-controller=\"Sales\" asp-action=\"CreateFromShipment\"", view);
        Assert.DoesNotContain("shipment-file-bulk-actions", view);
        Assert.DoesNotContain("shipment-file-row-actions", view);
        Assert.DoesNotContain("shipment-file-shell", view);
    }

    [Fact]
    public void Details_Record_Tables_Use_Operations_List_Design_System()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        // نوار عملیات گروهی disabled و چک‌باکس‌ها حذف شدند.
        Assert.DoesNotContain("data-shipment-batch-bar", view);
        // جدول‌های این صفحه هم مثل بقیهٔ لیست‌های سیستم از جدول مشترک استفاده می‌کنند.
        Assert.Contains("class=\"ak-table ak-detail-table\"", view);
        Assert.Contains("class=\"ak-col-actions no-print\"", view);
        Assert.Contains("~/Views/Shared/Partials/_Capsule.cshtml", view);
        Assert.Contains("class=\"ak-row-menu-btn\"", view);
        Assert.DoesNotContain("class=\"form-check-input\"", view);
        Assert.Contains("class=\"dropdown ak-row-menu\"", view);
        Assert.DoesNotContain("class=\"ak-row-actions\"", view);
        Assert.DoesNotContain("shipment-file-actions-col", view);
    }

    [Fact]
    public void Details_All_Tab_Record_Tables_Use_The_Shared_Component()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        // هیچ اثری از کامپوننت موازیِ قدیمی این صفحه نمانده است.
        Assert.DoesNotContain("shipment-record", view);

        // هر لیست تب داخل shell مشترک سیستم است: بخش + سرِ بخش + قاب جدول.
        Assert.Contains("class=\"ak-section-head ak-detail-section-header\"", view);
        Assert.True(
            Regex.Matches(view, "class=\"ak-table-wrap\"").Count >= 5,
            "Every tab record list must sit inside the shared .ak-table-wrap shell.");
        Assert.True(
            Regex.Matches(view, "class=\"ak-table ak-detail-table\"").Count >= 5,
            "Every tab record list must render the shared .ak-table.");

        // ستون‌ها و سلول‌ها از واژگان مشترک سیستم می‌آیند، نه از کلاس‌های اختصاصی.
        Assert.Contains("class=\"ak-col-grow\"", view);
        Assert.Contains("class=\"ak-num ak-col-num\"", view);
        Assert.Contains("class=\"ak-cell-note\"", view);
        Assert.Contains("<div class=\"ak-empty\">", view);

        // کارت «منابع بار» با نوار سهم نمایش داده می‌شود، نه جدول عددیِ شلوغ.
        Assert.Contains("sf-source-list", view);
        Assert.Contains("class=\"sf-bar\"", view);
    }

    [Fact]
    public void Details_Tabs_Use_The_Shared_Client_Pager()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");
        var tablesJs = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js");

        // صفحه‌بندی اختصاصی این صفحه حذف شده است.
        Assert.DoesNotContain("shipmentPagerReady", view);
        Assert.DoesNotContain("ak-pager", view);

        // و کامپوننت مشترک سیستم جدول‌های این صفحه را هم پوشش می‌دهد.
        Assert.Contains("table.ak-table", tablesJs);
        Assert.Contains("ptg-client-pager", tablesJs);
    }

    [Fact]
    public void Shipment_File_Has_No_Parallel_Record_List_Design_System()
    {
        // فایل، TagHelper، مدل‌های سلول و partialهای کامپوننت موازی حذف شده‌اند.
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/wwwroot/css/ptg/64-shipment-record-list.css"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/TagHelpers/ShipmentRecordListTagHelper.cs"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Models/ShipmentPnl/ShipmentRecordCellModels.cs"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ShipmentPnl/Partials/_ShipmentRecordEntity.cshtml"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ShipmentPnl/Partials/_ShipmentRecordStatus.cshtml"));
        Assert.False(RepoFileExists("src/PTGOilSystem.Web/Views/ShipmentPnl/Partials/_ShipmentRecordEmpty.cshtml"));

        // و هیچ ارجاعی به آن در پوسته، CSS مشترک یا JS مشترک باقی نمانده است.
        Assert.DoesNotContain("64-shipment-record-list.css", ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml"));
        Assert.DoesNotContain("shipment-record", ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/50-ak-components.css"));
        Assert.DoesNotContain("shipment-record", ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/71-typography.css"));
        Assert.DoesNotContain("shipment-record", ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/73-detail-system.css"));
        Assert.DoesNotContain("shipment-record", ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/tables.js"));
    }

    [Fact]
    public void Details_ViewModel_Calculates_Whole_Shipment_Result_From_Full_Costs()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 40m,
            DirectLossQuantityMt = 10m,
            TotalSalesUsd = 6_000m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        Assert.Equal(40m, model.SoldQuantityMt);
        Assert.Equal(50m, model.RemainingUnsoldQuantityMt);
        Assert.Equal(4_000m, model.RealizedPurchaseCostUsd);
        Assert.Equal(400m, model.RealizedOperationalExpensesUsd);
        Assert.Equal(1_600m, model.RealizedGrossMarginUsd);
        Assert.Equal(-5_000m, model.ShipmentNetResultUsd);
        Assert.False(model.NetResultIsProfit);
    }

    [Fact]
    public void Details_View_Uses_Actual_Source_Tank_Remaining_For_Remaining_Kpi()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        Assert.Contains("باقی‌مانده در مخزن", view);
        Assert.Contains("Model.RemainingInSourceTankQuantityMt", view);
    }

    [Fact]
    public void Details_ViewModel_Does_Not_Double_Deduct_Company_Loss_From_Whole_Shipment_Result()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 40m,
            CompanyLossQuantityMt = 10m,
            TotalSalesUsd = 6_000m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        // بهای جنس ضایع‌شده: 10 × 100 = 1000
        Assert.Equal(1_000m, model.CompanyLossPurchaseCostUsd);
        // سهم مصارف ضایعات: 1000 × (10/100) = 100
        Assert.Equal(100m, model.CompanyLossExpenseShareUsd);
        Assert.Equal(1_100m, model.CompanyLossDeductionUsd);
        // محاسبهٔ تحقق‌یافتهٔ قدیمی برای سازگاری داخلی باقی است.
        Assert.Equal(500m, model.RealizedGrossMarginUsd);
        // نتیجهٔ رسمی کل محموله: 6000 − 10000 − 1000 = −5000.
        // ثبت «ضرر شرکت» قیمت همان بار را دوباره کم نمی‌کند.
        Assert.Equal(-5_000m, model.ShipmentNetResultUsd);
    }

    [Fact]
    public void Related_Transport_Forms_Use_Modern_Form_Shells()
    {
        var receipt = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportReceipts/Create.cshtml");
        var sale = ReadRepoFile("src/PTGOilSystem.Web/Views/Sales/CreateFromShipment.cshtml");
        var saleForm = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Components/Ak/_ShipmentSaleForm.cshtml");
        var expense = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/CreateGroupExpense.cshtml");
        var customs = ReadRepoFile("src/PTGOilSystem.Web/Views/CustomsDeclarations/Create.cshtml");
        var loss = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Create.cshtml");
        var lossEdit = ReadRepoFile("src/PTGOilSystem.Web/Views/LossEvents/Edit.cshtml");

        Assert.Contains("class=\"ak-form\"", receipt);
        Assert.Contains("_AkPageHeader", receipt);
        Assert.DoesNotContain("ds-form-shell", receipt);
        Assert.DoesNotContain("operations-one-page-form", receipt);
        // فروش از جریان حمل، پوستهٔ اختصاصی خودش (ShipmentSaleForm) را دارد.
        Assert.Contains("_ShipmentSaleForm", sale);
        Assert.DoesNotContain("ds-form-shell", sale);
        Assert.DoesNotContain("operations-one-page-form", sale);
        Assert.Contains("data-shipment-sale-form", saleForm);
        Assert.Contains("بار قابل فروش در جریان", saleForm);
        Assert.DoesNotContain(">بارگیری‌شده<", saleForm);
        Assert.Contains("class=\"ak-form\"", expense);
        Assert.Contains("_AkPageHeader", expense);
        Assert.DoesNotContain("ds-form-shell", expense);
        Assert.DoesNotContain("operations-one-page-form", expense);
        Assert.Contains("class=\"ak-form\"", customs);
        Assert.Contains("_AkPageHeader", customs);
        Assert.DoesNotContain("ds-form-shell", customs);
        Assert.DoesNotContain("operations-one-page-form", customs);
        Assert.Contains("ak-form", loss);
        Assert.Contains("_AkPageHeader", loss);
        Assert.Contains("class=\"ak-form\"", lossEdit);
        Assert.Contains("_AkPageHeader", lossEdit);
        Assert.DoesNotContain("ds-card", lossEdit);
    }

    [Fact]
    public void TransportDisplayRows_Groups_50_Truck_Legs_Into_One_Batch_Row()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            TransportLegs = Enumerable.Range(1, 50)
                .Select(index => new ShipmentPnlTransportLegItemViewModel
                {
                    Id = index,
                    TransportGroupKey = "ITG:BATCH-50",
                    TransportReference = $"PL-{index:000}",
                    LoadedDate = new DateTime(2026, 6, 29),
                    QuantityMt = 30m
                })
                .ToList()
        };

        var row = Assert.Single(model.TransportDisplayRows);
        Assert.Equal(50, row.Items.Count);
        Assert.Equal(1_500m, row.QuantityMt);
    }

    [Fact]
    public async Task Details_Separates_Vessel_Unload_From_Later_Inventory_Transport_And_Delivery()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 10_000m;
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "SRC", Name = "Source terminal" },
            new Terminal { Id = 2, Code = "DST", Name = "Destination terminal" });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-01", CapacityMt = 20_000m },
            new StorageTank { Id = 2, TerminalId = 2, ProductId = 1, TankCode = "TK-02", CapacityMt = 20_000m });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-10000",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 10_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 10_000m });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 1, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 2), QuantityMt = 10_000m
            },
            new InventoryMovement
            {
                Id = 2, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.Out, MovementDate = new DateTime(2026, 6, 3), QuantityMt = 2_000m
            });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 1, DestinationStorageTankId = 1,
                TransportType = LoadingTransportType.Vessel, LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 10_000m, Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 101, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, DestinationTerminalId = 2, DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Truck, LoadedDate = new DateTime(2026, 6, 3),
                QuantityMt = 2_000m, Status = InventoryTransportLegStatus.Loaded, OutboundInventoryMovementId = 2
            });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 200,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 6, 2),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            ReceivedQuantityMt = 10_000m,
            InventoryMovementId = 1,
            Notes = "Group receipt: SHIP:1 | Total loaded: 10,000.0000 MT"
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);
        var loadedView = Assert.IsType<ViewResult>(await controller.Details(1));
        var loaded = Assert.IsType<ShipmentPnlDetailsViewModel>(loadedView.Model);

        Assert.Equal(10_000m, loaded.OriginalShipmentQuantityMt);
        Assert.Equal(10_000m, loaded.VesselUnloadedQuantityMt);
        var registeredReceipt = Assert.Single(loaded.RegisteredVesselReceipts);
        Assert.Equal(200, registeredReceipt.Id);
        Assert.Equal(10_000m, loaded.RegisteredVesselReceiptQuantityMt);
        Assert.Equal("PUR-10000", registeredReceipt.ContractNumber);
        Assert.Equal("Source terminal", registeredReceipt.DestinationTerminalName);
        Assert.Equal("TK-01", registeredReceipt.DestinationTankName);
        Assert.Equal(2_000m, loaded.InventoryTransportedOutQuantityMt);
        Assert.Equal(2_000m, loaded.InTransitQuantityMt);
        Assert.Equal(0m, loaded.DeliveredAtDestinationQuantityMt);
        Assert.Equal(8_000m, loaded.RemainingInSourceTankQuantityMt);
        Assert.Equal(10_000m, loaded.ContractLines.Single().UsedQuantityMt);
        Assert.Equal(2_000m, loaded.ContractLines.Single().TransportedFromInventoryQuantityMt);
        Assert.Equal(1_000_000m, loaded.TotalPurchaseCostUsd);

        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 3, ContractId = 2, ProductId = 1, TerminalId = 2, StorageTankId = 2,
            Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 4), QuantityMt = 2_000m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 201,
            InventoryTransportLegId = 101,
            ReceiptDate = new DateTime(2026, 6, 4),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 2,
            DestinationStorageTankId = 2,
            ReceivedQuantityMt = 2_000m,
            InventoryMovementId = 3
        });
        db.InventoryTransportLegs.Local.Single(l => l.Id == 101).Status = InventoryTransportLegStatus.Received;
        await db.SaveChangesAsync();

        var deliveredView = Assert.IsType<ViewResult>(await controller.Details(1));
        var delivered = Assert.IsType<ShipmentPnlDetailsViewModel>(deliveredView.Model);
        Assert.Equal(10_000m, delivered.OriginalShipmentQuantityMt);
        Assert.Equal(10_000m, delivered.VesselUnloadedQuantityMt);
        Assert.Equal(2_000m, delivered.InventoryTransportedOutQuantityMt);
        Assert.Equal(0m, delivered.InTransitQuantityMt);
        Assert.Equal(2_000m, delivered.DeliveredAtDestinationQuantityMt);
        Assert.Equal(8_000m, delivered.RemainingInSourceTankQuantityMt);
    }

    [Fact]
    public async Task Inventory_Trip_Quantities_Use_Only_Posted_Vehicle_Legs_And_Close_The_Equation()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 10_000m;
        db.Terminals.AddRange(
            new Terminal { Id = 1, Code = "SRC", Name = "Source terminal" },
            new Terminal { Id = 2, Code = "DST", Name = "Destination terminal" });
        db.StorageTanks.AddRange(
            new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-01", CapacityMt = 20_000m },
            new StorageTank { Id = 2, TerminalId = 2, ProductId = 1, TankCode = "TK-02", CapacityMt = 20_000m });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-10000",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 10_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 10_000m });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 1, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 2), QuantityMt = 10_000m
            },
            new InventoryMovement
            {
                Id = 2, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.Out, MovementDate = new DateTime(2026, 6, 3), QuantityMt = 2_000m
            },
            new InventoryMovement
            {
                Id = 3, ContractId = 2, ProductId = 1, TerminalId = 1,
                Direction = MovementDirection.Out, MovementDate = new DateTime(2026, 6, 3), QuantityMt = 700m
            });
        db.InventoryTransportLegs.AddRange(
            // مرحلهٔ ورودِ محموله (کشتی → مخزن): سفر نیست.
            new InventoryTransportLeg
            {
                Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 1, DestinationStorageTankId = 1,
                TransportType = LoadingTransportType.Vessel, LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 10_000m, Status = InventoryTransportLegStatus.Received
            },
            // سفر واقعی و ثبت‌شده روی موجودی، با رسید ناقص (۱٬۸۰۰ تخلیه + ۱۰۰ کسری).
            new InventoryTransportLeg
            {
                Id = 101, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, DestinationTerminalId = 2, DestinationStorageTankId = 2,
                TransportType = LoadingTransportType.Truck, LoadedDate = new DateTime(2026, 6, 3),
                QuantityMt = 2_000m, Status = InventoryTransportLegStatus.Received, OutboundInventoryMovementId = 2
            },
            // سفر پیش‌نویس: در جدول دیده می‌شود ولی روی موجودی ننشسته است.
            new InventoryTransportLeg
            {
                Id = 102, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Truck, LoadedDate = new DateTime(2026, 6, 4),
                QuantityMt = 500m, Status = InventoryTransportLegStatus.Draft
            },
            // سفر بدون مبدأ مخزن: قبلاً از جدول و شمارش می‌افتاد ولی مقدارش در کارت‌ها بود.
            new InventoryTransportLeg
            {
                Id = 103, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Wagon, LoadedDate = new DateTime(2026, 6, 5),
                QuantityMt = 300m, Status = InventoryTransportLegStatus.Loaded
            },
            // legِ منبع (Unspecified، بدون وسیله): سفر نیست و نباید در هیچ کارت این تب بیاید.
            new InventoryTransportLeg
            {
                Id = 104, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Unspecified, LoadedDate = new DateTime(2026, 6, 6),
                QuantityMt = 700m, Status = InventoryTransportLegStatus.Received, OutboundInventoryMovementId = 3
            },
            // سفر لغوشده: از همه‌جا بیرون است.
            new InventoryTransportLeg
            {
                Id = 105, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, DestinationTerminalId = 2,
                TransportType = LoadingTransportType.Truck, LoadedDate = new DateTime(2026, 6, 7),
                QuantityMt = 400m, Status = InventoryTransportLegStatus.Cancelled
            });
        db.InventoryTransportReceipts.AddRange(
            new InventoryTransportReceipt
            {
                Id = 200, InventoryTransportLegId = 100, ReceiptDate = new DateTime(2026, 6, 2),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 1, DestinationStorageTankId = 1,
                ReceivedQuantityMt = 10_000m, InventoryMovementId = 1
            },
            new InventoryTransportReceipt
            {
                Id = 201, InventoryTransportLegId = 101, ReceiptDate = new DateTime(2026, 6, 4),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 2, DestinationStorageTankId = 2,
                ReceivedQuantityMt = 1_800m, ShortageQuantityMt = 100m
            },
            new InventoryTransportReceipt
            {
                Id = 202, InventoryTransportLegId = 104, ReceiptDate = new DateTime(2026, 6, 6),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 2,
                ReceivedQuantityMt = 700m
            });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);
        var view = Assert.IsType<ViewResult>(await controller.Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        // فقط سفرِ واقعیِ موتر/واگون که روی موجودی نشسته (۱۰۱) مقدار می‌سازد:
        // پیش‌نویس (۱۰۲)، سفر بدون خروج ثبت‌شده (۱۰۳)، legِ منبع Unspecified (۱۰۴)
        // و سفر لغوشده (۱۰۵) هیچ‌کدام در این سه عدد نمی‌آیند.
        Assert.Equal(2_000m, model.InventoryTransportedOutQuantityMt);
        Assert.Equal(1_800m, model.DeliveredAtDestinationQuantityMt);
        Assert.Equal(100m, model.InTransitQuantityMt);
        Assert.Equal(100m, model.InventoryTransportShortageQuantityMt);
        // معادلهٔ بستهٔ تب سفرها.
        Assert.Equal(
            model.InventoryTransportedOutQuantityMt,
            model.InTransitQuantityMt
                + model.DeliveredAtDestinationQuantityMt
                + model.InventoryTransportShortageQuantityMt);
        Assert.Equal(500m, model.DraftTransportQuantityMt);
        Assert.Equal(400m, model.CancelledTransportQuantityMt);
    }

    [Fact]
    public async Task Details_Uses_Registered_Shipment_Group_Receipt_When_Leg_Type_Is_Unspecified()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 3_906.531m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "AKR", Name = "ترمینال اگریم" });
        db.StorageTanks.Add(new StorageTank
        {
            Id = 1,
            TerminalId = 1,
            ProductId = 1,
            TankCode = "AKR-01",
            DisplayName = "اکریم",
            CapacityMt = 10_000m
        });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-WALGA",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 7, 1),
            QuantityMt = 3_906.531m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract
        {
            ShipmentId = 1,
            ContractId = 2,
            QuantityMt = 3_906.531m
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1,
            ContractId = 2,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 7, 2),
            QuantityMt = 3_888.880m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 7, 1),
            QuantityMt = 3_906.531m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 200,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 7, 2),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 1,
            DestinationStorageTankId = 1,
            ReceivedQuantityMt = 3_888.880m,
            InventoryMovementId = 1,
            Notes = "Group receipt: SHIP:1 | Total received: 3,888.8800 MT"
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(3_888.880m, model.VesselUnloadedQuantityMt);
        Assert.Equal(3_888.880m, model.RemainingInSourceTankQuantityMt);
        Assert.Equal(3_888.880m, model.RegisteredVesselReceiptQuantityMt);

        // لِجِ ریشه با نوع Unspecified حرکت خروجیِ موجودی ندارد (بار از کشتی می‌آید، نه از مخزن)،
        // ولی رسید تخلیهٔ کشتی روی آن ثبت شده است؛ پس باید ریشه شناخته شود.
        Assert.Equal(3_906.531m, model.ContractLines.Single().UsedQuantityMt);
        // لِج هنوز باز است (Loaded): 17.651 مصرف‌نشده هنوز روی کشتی است، نه کسری.
        Assert.Equal(0m, model.VesselUnloadingShortageQuantityMt);
        Assert.Equal(17.651m, model.NotYetDischargedQuantityMt);
        // کسری تخلیه نباید دوباره زیر «کسری حمل داخلی» هم بیاید.
        Assert.Equal(0m, model.InventoryTransportShortageQuantityMt);
        Assert.Equal(0m, model.TotalLossAndShortageMt);
        // رسیدِ تخلیه، حملِ داخلی نیست.
        Assert.Equal(0m, model.DeliveredAtDestinationQuantityMt);
        Assert.Equal(0m, model.InTransitQuantityMt);

        // تخلیهٔ لِج بسته می‌شود بدون آنکه کسری ثبت شده باشد (دادهٔ قدیمی):
        // حالا همان 17.651 «کسری خاموش» است و رابطهٔ بسته برقرار می‌ماند.
        db.InventoryTransportLegs.Local.Single(l => l.Id == 100).Status = InventoryTransportLegStatus.Received;
        await db.SaveChangesAsync();

        var closedView = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var closed = Assert.IsType<ShipmentPnlDetailsViewModel>(closedView.Model);
        Assert.Equal(3_888.880m, closed.VesselUnloadedQuantityMt);
        Assert.Equal(17.651m, closed.VesselUnloadingShortageQuantityMt);
        Assert.Equal(0m, closed.InventoryTransportShortageQuantityMt);
        Assert.Equal(17.651m, closed.TotalLossAndShortageMt);
        // بار کل = تخلیه‌شده + کسری تخلیه + تخلیه‌نشده.
        Assert.Equal(0m, closed.NotYetDischargedQuantityMt);
        // ظرفیت ثبت کسری دستی = آنچه واقعاً تخلیه شده است.
        Assert.Equal(3_888.880m, closed.TotalShortageRegisterableQuantityMt);
    }

    [Fact]
    public async Task Details_Does_Not_Report_Undischarged_Vessel_Cargo_As_Shortage_During_Partial_Discharge()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 10_000m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-01", CapacityMt = 20_000m });
        db.Contracts.Add(new Contract
        {
            Id = 2, ContractNumber = "PUR-10000", ContractType = ContractType.Purchase,
            CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 10_000m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 10_000m });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
            Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 2), QuantityMt = 2_990m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
            SourceTerminalId = 1, DestinationTerminalId = 1, DestinationStorageTankId = 1,
            TransportType = LoadingTransportType.Vessel, LoadedDate = new DateTime(2026, 6, 1),
            QuantityMt = 10_000m, Status = InventoryTransportLegStatus.Loaded
        });
        // تخلیهٔ مرحلهٔ اول: 2,990 رسید + 10 کسری. 7,000 هنوز داخل کشتی است.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 200, InventoryTransportLegId = 100, ReceiptDate = new DateTime(2026, 6, 2),
            ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
            DestinationTerminalId = 1, DestinationStorageTankId = 1,
            ReceivedQuantityMt = 2_990m, ShortageQuantityMt = 10m, InventoryMovementId = 1
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(2_990m, model.VesselUnloadedQuantityMt);
        // فقط کسریِ واقعاً ثبت‌شده. 7,000 باقیمانده هنوز روی کشتی است، نه کسری.
        Assert.Equal(10m, model.VesselUnloadingShortageQuantityMt);
        Assert.Equal(10m, model.TotalLossAndShortageMt);
        Assert.Equal(7_000m, model.NotYetDischargedQuantityMt);
    }

    [Fact]
    public async Task Details_Excludes_Cancelled_And_Separates_Draft_Inventory_Transports()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 1_000m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-01", CapacityMt = 2_000m });
        db.Contracts.Add(new Contract
        {
            Id = 2, ContractNumber = "PUR-1000", ContractType = ContractType.Purchase,
            CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 1_000m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 1_000m });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
            Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 2), QuantityMt = 1_000m
        });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 1, DestinationStorageTankId = 1,
                TransportType = LoadingTransportType.Vessel, LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 1_000m, Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 101, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 6, 3), QuantityMt = 300m,
                Status = InventoryTransportLegStatus.Cancelled
            },
            new InventoryTransportLeg
            {
                Id = 102, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, TransportType = LoadingTransportType.Wagon,
                LoadedDate = new DateTime(2026, 6, 3), QuantityMt = 400m,
                Status = InventoryTransportLegStatus.Draft
            });
        db.InventoryTransportReceipts.AddRange(
            new InventoryTransportReceipt
            {
                Id = 200, InventoryTransportLegId = 100, ReceiptDate = new DateTime(2026, 6, 2),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 1, DestinationStorageTankId = 1,
                ReceivedQuantityMt = 1_000m, InventoryMovementId = 1
            },
            new InventoryTransportReceipt
            {
                Id = 201, InventoryTransportLegId = 101, ReceiptDate = new DateTime(2026, 6, 4),
                ReceivedQuantityMt = 300m
            });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        Assert.Equal(1_000m, model.OriginalShipmentQuantityMt);
        Assert.Equal(1_000m, model.VesselUnloadedQuantityMt);
        Assert.Equal(0m, model.InventoryTransportedOutQuantityMt);
        Assert.Equal(0m, model.InTransitQuantityMt);
        Assert.Equal(0m, model.DeliveredAtDestinationQuantityMt);
        Assert.Equal(1_000m, model.RemainingInSourceTankQuantityMt);
        Assert.Equal(300m, model.CancelledTransportQuantityMt);
        Assert.Equal(400m, model.DraftTransportQuantityMt);
    }

    [Fact]
    public async Task Index_Original_Quantity_Falls_Back_To_Contract_Allocations_Not_Transport_Legs()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 0m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2, ContractNumber = "PUR-ALLOC", ContractType = ContractType.Purchase,
            CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 1_000m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 1_000m });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 6, 1), QuantityMt = 1_000m, Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 101, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, TransportType = LoadingTransportType.Truck,
                LoadedDate = new DateTime(2026, 6, 2), QuantityMt = 200m, Status = InventoryTransportLegStatus.Loaded
            });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Index());
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        Assert.Equal(1_000m, Assert.Single(model.Items).QuantityMt);
    }

    [Fact]
    public async Task Index_Returns_Shipment_Summary_From_Direct_Relations()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = "INV-1",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 3,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            SaleStage = SaleStage.PreSale,
            InvoiceNumber = "PRE-SALE-SHIPMENT",
            SaleDate = new DateTime(2026, 4, 21),
            QuantityMt = 100m,
            UnitPriceUsd = 1_000m,
            TotalUsd = 100_000m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 23),
            AmountUsd = 700m,
            Description = "PORT-1"
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 23),
            Side = LedgerSide.Credit,
            AmountUsd = 5000m,
            Currency = "USD",
            Description = "Sale ledger",
            SourceType = "Sale",
            SourceId = 1,
            ShipmentId = 1
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlIndexViewModel>(view.Model);
        var item = Assert.Single(model.Items);
        Assert.Equal("SHIP-01", item.ShipmentCode);
        Assert.Equal(5000m, item.TotalSalesUsd);
        Assert.Equal(1, item.RelatedSalesCount);
        Assert.Equal(1, item.RelatedExpensesCount);

        // ارقام بهای تمام‌شده و نتیجهٔ مالی از لیست برداشته شده‌اند (لیست باید سبک بماند)
        // اما در همان رول‌آپی که پروندهٔ محموله و خروجی از آن می‌خوانند دست‌نخورده‌اند.
        var rollupItem = Assert.Single(await controller.BuildAllIndexItemsAsync());
        Assert.Equal(5000m, rollupItem.TotalSalesUsd);
        Assert.Equal(700m, rollupItem.TotalExpensesUsd);
        Assert.Equal(4300m, rollupItem.GrossMarginUsd);
        Assert.Equal(1, rollupItem.RelatedLedgerCount);
    }

    [Fact]
    public async Task Details_Uses_Only_Direct_Shipment_Relations_And_Does_Not_Guess()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.SalesTransactions.AddRange(
            new SalesTransaction
            {
                Id = 1,
                CompanyId = 1,
                ContractId = 1,
                CustomerId = 1,
                ProductId = 1,
                ShipmentId = 1,
                InvoiceNumber = "INV-1",
                SaleDate = new DateTime(2026, 4, 22),
                QuantityMt = 10m,
                UnitPriceUsd = 500m,
                TotalUsd = 5000m
            },
            new SalesTransaction
            {
                Id = 2,
                CompanyId = 1,
                ContractId = 1,
                CustomerId = 1,
                ProductId = 1,
                ShipmentId = null,
                InvoiceNumber = "INV-OTHER",
                SaleDate = new DateTime(2026, 4, 23),
                QuantityMt = 5m,
                UnitPriceUsd = 200m,
                TotalUsd = 1000m
            });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                ExpenseTypeId = 1,
                ContractId = 1,
                ShipmentId = 1,
                ExpenseDate = new DateTime(2026, 4, 23),
                AmountUsd = 700m,
                Description = "PORT-1"
            },
            new ExpenseTransaction
            {
                Id = 2,
                ExpenseTypeId = 1,
                ContractId = 1,
                ShipmentId = null,
                ExpenseDate = new DateTime(2026, 4, 24),
                AmountUsd = 200m,
                Description = "UNLINKED"
            });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 1,
                ShipmentId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 23),
                Side = LedgerSide.Debit,
                AmountUsd = 200m,
                Currency = "USD",
                Description = "Unlinked ledger",
                SourceType = "Expense",
                SourceId = 2,
                ShipmentId = null,
                ContractId = 1
            });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        Assert.Equal(5000m, model.TotalSalesUsd);
        Assert.Equal(700m, model.TotalExpensesUsd);
        Assert.Equal(4300m, model.GrossMarginUsd);
        Assert.Single(model.Sales);
        Assert.Single(model.Expenses);
        Assert.Single(model.LedgerEntries);
    }

    [Fact]
    public async Task Details_Includes_TransportLeg_Related_Sale_Expense_And_Purchase_Cost()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 10m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 10m });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            WagonNumber = "KALUGA",
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 10m,
            PurchaseUnitCostUsd = 400m,
            Status = InventoryTransportLegStatus.Received
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 2,
            CompanyId = 1,
            ContractId = null,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = null,
            InvoiceNumber = "INV-LEG",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 50,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 4, 22),
            ReceivedQuantityMt = 10m,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            SalesTransactionId = 2
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "Port" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 2,
            ExpenseTypeId = 1,
            ContractId = 2,
            ShipmentId = null,
            TransportLegId = 100,
            ExpenseDate = new DateTime(2026, 4, 23),
            AmountUsd = 700m,
            Description = "PORT-LEG"
        });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 10,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Credit,
                AmountUsd = 5000m,
                Currency = "USD",
                Description = "Sale ledger",
                SourceType = "Sale",
                SourceId = 2,
                ShipmentId = null
            },
            new LedgerEntry
            {
                Id = 11,
                EntryDate = new DateTime(2026, 4, 23),
                Side = LedgerSide.Debit,
                AmountUsd = 700m,
                Currency = "USD",
                Description = "Expense ledger",
                SourceType = "Expense",
                SourceId = 2,
                ShipmentId = null
            });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        Assert.Equal(5000m, model.TotalSalesUsd);
        Assert.Equal(4000m, model.TotalPurchaseCostUsd);
        Assert.Equal(700m, model.TotalOperationalExpensesUsd);
        Assert.Equal(4700m, model.TotalExpensesUsd);
        Assert.Equal(300m, model.GrossMarginUsd);
        Assert.Equal(300m, model.ShipmentNetResultUsd);
        var leg = Assert.Single(model.TransportLegs);
        Assert.Equal(10m, leg.SoldQuantityMt);
        Assert.Equal(5000m, leg.SalesUsd);
        Assert.Equal(700m, leg.OperationalExpensesUsd);
        Assert.Equal(4700m, leg.TotalCostUsd);
        Assert.Equal(300m, leg.GrossMarginUsd);
        Assert.Single(model.Sales);
        Assert.Single(model.Expenses);
        Assert.Equal(2, model.LedgerEntries.Count);
    }

    [Fact]
    public async Task Details_Includes_PerLeg_Pnl_With_Freight_Customs_And_Loss()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-FRT",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 10m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            RwbNo = "RWB-PNL",
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 10m,
            PurchaseUnitCostUsd = 100m,
            Status = InventoryTransportLegStatus.Received
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 20,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = "INV-PNL",
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 9m,
            UnitPriceUsd = 200m,
            TotalUsd = 1_800m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 30,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 4, 23),
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            ReceivedQuantityMt = 9m,
            ShortageQuantityMt = 1m,
            ChargeableShortageMt = 1m,
            FreightCostUsd = 50m,
            ShortageChargeUsd = 10m,
            FreightPayableUsd = 40m,
            SalesTransactionId = 20
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "OPS", Name = "Operations" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 40,
            ExpenseTypeId = 1,
            ContractId = 2,
            TransportLegId = 100,
            ExpenseDate = new DateTime(2026, 4, 23),
            AmountUsd = 100m
        });
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 50,
            TransportLegId = 100,
            DeclarationDate = new DateTime(2026, 4, 23),
            TotalUsd = 60m
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var leg = Assert.Single(model.TransportLegs);
        Assert.Equal(1_800m, model.TotalSalesUsd);
        Assert.Equal(1_000m, model.TotalPurchaseCostUsd);
        Assert.Equal(200m, model.TotalOperationalExpensesUsd);
        Assert.Equal(1_200m, model.TotalExpensesUsd);
        Assert.Equal(600m, model.GrossMarginUsd);
        Assert.Equal(9m, leg.SoldQuantityMt);
        Assert.Equal(1m, leg.ShortageQuantityMt);
        Assert.Equal(200m, leg.OperationalExpensesUsd);
        Assert.Equal(1_200m, leg.TotalCostUsd);
        Assert.Equal(600m, leg.GrossMarginUsd);
    }

    [Fact]
    public async Task Details_When_TransportLegs_Exist_Uses_Leg_Purchase_Cost_For_Shipment_Pnl()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-ALLOC",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            WagonNumber = "REAL-LOAD",
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 10m,
            PurchaseUnitCostUsd = 400m,
            Status = InventoryTransportLegStatus.Received
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 2,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "INV-REAL-LOAD",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 50,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 4, 22),
            ReceivedQuantityMt = 10m,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            SalesTransactionId = 2
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "Port" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 2,
            ExpenseTypeId = 1,
            ContractId = 2,
            ShipmentId = null,
            TransportLegId = 100,
            ExpenseDate = new DateTime(2026, 4, 23),
            AmountUsd = 700m,
            Description = "PORT-LEG"
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        Assert.Equal(5000m, model.TotalSalesUsd);
        Assert.Equal(4000m, model.TotalPurchaseCostUsd);
        Assert.Equal(700m, model.TotalOperationalExpensesUsd);
        Assert.Equal(4700m, model.TotalExpensesUsd);
        Assert.Equal(300m, model.GrossMarginUsd);
        Assert.Single(model.TransportLegs);
        Assert.Equal(50m, model.ContractLines.Single().AllocatedQuantityMt);
    }

    [Fact]
    public async Task Details_Returns_Dispatch_Trace_Only_Via_DeliveryReceipt()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "AFG-001" });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 3,
            ContractId = 1,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 4, 25),
            LoadedQuantityMt = 20m
        });
        db.DeliveryReceipts.Add(new DeliveryReceipt
        {
            Id = 9,
            ShipmentId = 1,
            TruckDispatchId = 3,
            ReceiptDate = new DateTime(2026, 4, 26),
            ReceivedQuantityMt = 19.5m,
            DocumentReference = "DR-9"
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var dispatch = Assert.Single(model.DispatchTraces);
        Assert.Equal(9, dispatch.DeliveryReceiptId);
        Assert.Equal(3, dispatch.TruckDispatchId);
        Assert.Equal("AFG-001", dispatch.TruckPlateNumber);
        Assert.Equal(19.5m, dispatch.ReceivedQuantityMt);
    }

    [Fact]
    public async Task Journey_Builds_ReadOnly_View_For_Shipments_From_Existing_Data()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ShipmentContracts.Add(new ShipmentContract
        {
            Id = 1,
            ShipmentId = 1,
            ContractId = 1,
            QuantityMt = 50m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = "INV-1",
            SaleDate = new DateTime(2026, 4, 24),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port", NamePersian = "هزینه بندری" });
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 1,
            ExpenseTypeId = 1,
            ContractId = 1,
            ShipmentId = 1,
            ExpenseDate = new DateTime(2026, 4, 25),
            AmountUsd = 700m,
            Description = "PORT-1"
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        // «نمای کشتی» در پروندهٔ مادر (Details) ادغام شد؛ Journey فقط هدایت می‌کند.
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Journey(1));
        Assert.Equal(nameof(ShipmentPnlController.Details), redirect.ActionName);

        // داده‌ها و اخطارهای نمای کشتی اکنون داخل Details در دسترس‌اند.
        var view = Assert.IsType<ViewResult>(await controller.Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(1, model.Id);
        Assert.Equal("SHIP-01", model.ShipmentCode);
        var line = Assert.Single(model.ContractLines);
        Assert.Equal(50m, line.AllocatedQuantityMt);
        Assert.Equal(5000m, model.TotalSalesUsd);
        Assert.Equal(10m, model.SoldQuantityMt);
        Assert.Single(model.Sales);
        Assert.NotEmpty(model.Expenses);
        // بدون Transport Leg مستقیم → باید هشدار بدهد، اما حدس نزند.
        Assert.Empty(model.TransportLegs);
        Assert.Contains(model.Warnings, w => w.Contains("جریان حمل"));
    }

    [Fact]
    public async Task Details_Lists_Shipment_Contracts_With_Used_And_Remaining()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });
        // یک حملِ غیرلغو به مقدار 20 (استفاده‌شده) + یک حملِ لغوشده که نباید شمرده شود.
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 20m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 101,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 15m,
                Status = InventoryTransportLegStatus.Cancelled
            });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var line = Assert.Single(model.ContractLines);
        Assert.Equal(2, line.ContractId);
        Assert.Equal(50m, line.AllocatedQuantityMt);
        Assert.Equal(20m, line.UsedQuantityMt);
        Assert.Equal(30m, line.RemainingQuantityMt);
        Assert.Equal(30m, model.TotalRemainingQuantityMt);
    }

    [Fact]
    public async Task Details_Tracks_Direct_Loss_Against_Loaded_Quantity_Without_Reducing_Untransported_Quantity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 20m,
            Status = InventoryTransportLegStatus.Loaded
        });
        db.LossEvents.Add(new LossEvent
        {
            ShipmentId = 1,
            ProductId = 1,
            Stage = LossEventStage.TransitLoss,
            EventDate = new DateTime(2026, 4, 22),
            ExpectedQuantityMt = 30m,
            ActualQuantityMt = 23m,
            DifferenceQuantityMt = 7m,
            ChargeableLossMt = 7m
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var line = Assert.Single(model.ContractLines);
        Assert.Equal(30m, line.RemainingBeforeLossQuantityMt);
        Assert.Equal(7m, line.DirectLossQuantityMt);
        Assert.Equal(30m, line.RemainingQuantityMt);
        Assert.Equal(13m, line.ShortageRegisterableQuantityMt);
        Assert.Equal(30m, model.TotalRemainingQuantityMt);
        Assert.Equal(13m, model.TotalShortageRegisterableQuantityMt);
        Assert.Equal(0m, model.AvailableForSaleOrReceiptQuantityMt);
        Assert.Equal(7m, model.DirectLossQuantityMt);
    }

    [Fact]
    public async Task Details_Counts_Manual_Losses_Registered_On_A_Leg_Dispatch_Or_Sale_And_Skips_The_Receipt_Mirror()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 1_000m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "TK-01", CapacityMt = 2_000m });
        db.Contracts.Add(new Contract
        {
            Id = 2, ContractNumber = "PUR-1000", ContractType = ContractType.Purchase,
            CompanyId = 1, SupplierId = 1, ProductId = 1, ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 1_000m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 100m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 1_000m });
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                Id = 1, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.In, MovementDate = new DateTime(2026, 6, 2), QuantityMt = 1_000m
            },
            new InventoryMovement
            {
                Id = 2, ContractId = 2, ProductId = 1, TerminalId = 1, StorageTankId = 1,
                Direction = MovementDirection.Out, MovementDate = new DateTime(2026, 6, 3), QuantityMt = 200m
            });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, DestinationTerminalId = 1, DestinationStorageTankId = 1,
                TransportType = LoadingTransportType.Vessel, LoadedDate = new DateTime(2026, 6, 1),
                QuantityMt = 1_000m, Status = InventoryTransportLegStatus.Received
            },
            new InventoryTransportLeg
            {
                Id = 101, ShipmentId = 1, SourcePurchaseContractId = 2, ProductId = 1,
                SourceTerminalId = 1, SourceStorageTankId = 1, DestinationTerminalId = 1,
                TransportType = LoadingTransportType.Truck, LoadedDate = new DateTime(2026, 6, 3),
                QuantityMt = 200m, Status = InventoryTransportLegStatus.Received,
                OutboundInventoryMovementId = 2
            });
        db.InventoryTransportReceipts.AddRange(
            new InventoryTransportReceipt
            {
                Id = 200, InventoryTransportLegId = 100, ReceiptDate = new DateTime(2026, 6, 2),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 1, DestinationStorageTankId = 1,
                ReceivedQuantityMt = 995m, ShortageQuantityMt = 5m, InventoryMovementId = 1
            },
            new InventoryTransportReceipt
            {
                Id = 201, InventoryTransportLegId = 101, ReceiptDate = new DateTime(2026, 6, 5),
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = 1, ReceivedQuantityMt = 197m, ShortageQuantityMt = 3m
            });
        db.LossEvents.AddRange(
            // آینهٔ خودکارِ کسری رسید — نباید دوباره شمرده شود.
            new LossEvent
            {
                ShipmentId = 1, ProductId = 1, ContractId = 2, TransportLegId = 100,
                Stage = LossEventStage.ReceiptShortage, EventDate = new DateTime(2026, 6, 2),
                ExpectedQuantityMt = 1_000m, ActualQuantityMt = 995m, DifferenceQuantityMt = 5m,
                Reference = "TRANSPORT-RECEIPT:200"
            },
            new LossEvent
            {
                ShipmentId = 1, ProductId = 1, ContractId = 2, TransportLegId = 101,
                Stage = LossEventStage.ReceiptShortage, EventDate = new DateTime(2026, 6, 5),
                ExpectedQuantityMt = 200m, ActualQuantityMt = 197m, DifferenceQuantityMt = 3m,
                Reference = "TRANSPORT-RECEIPT:201"
            },
            // کسری دستی با دکمهٔ «ثبت کسری» روی همان سفر — رسیدی پشتش نیست، پس تکراری نیست.
            new LossEvent
            {
                ShipmentId = 1, ProductId = 1, ContractId = 2, TransportLegId = 101,
                Stage = LossEventStage.TransitLoss, EventDate = new DateTime(2026, 6, 6),
                ExpectedQuantityMt = 197m, ActualQuantityMt = 193m, DifferenceQuantityMt = 4m
            },
            // کسری کشف‌شده هنگام فروش — باز هم کسری است، نه فروش.
            new LossEvent
            {
                ShipmentId = 1, ProductId = 1, ContractId = 2, SalesTransactionId = null,
                TruckDispatchId = null, Stage = LossEventStage.TankNaturalLoss,
                EventDate = new DateTime(2026, 6, 7), ExpectedQuantityMt = 100m,
                ActualQuantityMt = 98m, DifferenceQuantityMt = 2m
            });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await new ShipmentPnlController(db).Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        Assert.Equal(5m, model.VesselUnloadingShortageQuantityMt);
        Assert.Equal(3m, model.InventoryTransportShortageQuantityMt);
        // ۴ + ۲ = کسری‌هایی که پیش‌تر در هیچ کارتی دیده نمی‌شدند.
        Assert.Equal(6m, model.DirectLossQuantityMt);
        // کارت «مجموع کسری و ضایعات» حالا هر سه سطل را دارد و آینهٔ رسیدها را دوبار نمی‌شمارد.
        Assert.Equal(14m, model.TotalLossAndShortageMt);
        // جدول همان تب همچنان هر چهار رویداد را نشان می‌دهد.
        Assert.Equal(14m, model.RecordedLossQuantityMt);
    }

    [Fact]
    public async Task RegisterDirectLoss_Uses_StockBacked_Unspecified_Root_When_Contract_Remaining_Is_Zero()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Shipments.Local.Single(s => s.Id == 1).QuantityMt = 4144m;
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 4144m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 4144m });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 900,
            ContractId = 2,
            ProductId = 1,
            TerminalId = 1,
            StorageTankId = 1,
            Direction = MovementDirection.Out,
            MovementDate = new DateTime(2026, 4, 21),
            QuantityMt = 4144m,
            ReferenceDocument = "TRANSPORT-LEG:100"
        });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            SourceStorageTankId = 1,
            TransportType = LoadingTransportType.Unspecified,
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 4144m,
            Status = InventoryTransportLegStatus.Loaded,
            OutboundInventoryMovementId = 900
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

        var result = await controller.RegisterDirectLoss(new ShipmentDirectLossCreateViewModel
        {
            ShipmentId = 1,
            EventDate = new DateTime(2026, 4, 22),
            LossQuantityMt = 34m,
            Notes = "Leakage"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Contains("/ShipmentPnl/Details", redirect.Url);
        var loss = Assert.Single(await db.LossEvents.ToListAsync());
        Assert.Equal(LossEventStage.TransitLoss, loss.Stage);
        Assert.Equal(1, loss.ShipmentId);
        Assert.Equal(2, loss.ContractId);
        Assert.Equal(34m, loss.DifferenceQuantityMt);
        Assert.Equal(4144m, loss.ExpectedQuantityMt);
        Assert.Equal(4110m, loss.ActualQuantityMt);
        Assert.Equal(ShipmentShortageResponsibilityTypes.CompanyLoss, loss.ResponsiblePartyType);
        Assert.False(string.IsNullOrWhiteSpace(loss.FinancialTreatment));
        Assert.False(loss.AffectsInventory);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());

        var details = Assert.IsType<ViewResult>(await controller.Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(details.Model);
        var contractLine = Assert.Single(model.ContractLines);
        Assert.Equal(4144m, contractLine.UsedQuantityMt);
        Assert.Equal(0m, contractLine.TransportedFromInventoryQuantityMt);
        Assert.Equal(0m, model.TotalRemainingQuantityMt);
        Assert.Equal(4110m, model.TotalShortageRegisterableQuantityMt);
        Assert.Equal(0m, model.AvailableForSaleOrReceiptQuantityMt);
        Assert.Equal(4110m, model.ShipShortageActualQuantityMt);
        Assert.Equal(13600m, model.EstimatedShortageValueUsd);
        Assert.Equal(1657600m, model.TotalPurchaseCostUsd);
        Assert.Equal(-1657600m, model.GrossMarginUsd);
        Assert.Equal(-1657600m, model.ShipmentNetResultUsd);

        var secondResult = await controller.RegisterDirectLoss(new ShipmentDirectLossCreateViewModel
        {
            ShipmentId = 1,
            EventDate = new DateTime(2026, 4, 23),
            LossQuantityMt = 4111m,
            Notes = "Must be rejected"
        });

        Assert.IsType<RedirectResult>(secondResult);
        Assert.Single(await db.LossEvents.ToListAsync());
        Assert.True(controller.TempData.ContainsKey("error"));
    }

    [Fact]
    public async Task Details_Estimates_Shipment_Loss_Value_Proportionally_By_Contracts()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.AddRange(
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-001",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 60m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            },
            new Contract
            {
                Id = 3,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 40m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 200m
            });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 60m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 40m });
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 101,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded
            });
        db.LossEvents.Add(new LossEvent
        {
            ShipmentId = 1,
            ProductId = 1,
            Stage = LossEventStage.TransitLoss,
            EventDate = new DateTime(2026, 4, 22),
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 90m,
            DifferenceQuantityMt = 10m,
            ChargeableLossMt = 10m
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        Assert.Equal(10m, model.ShipShortageQuantityMt);
        Assert.Equal(10m, model.DirectLossQuantityMt);
        Assert.Equal(1400m, model.EstimatedShortageValueUsd);
        Assert.Equal(6m, model.ContractLines.Single(l => l.ContractId == 2).DirectLossQuantityMt);
        Assert.Equal(4m, model.ContractLines.Single(l => l.ContractId == 3).DirectLossQuantityMt);
    }

    [Fact]
    public async Task RegisterDirectLoss_Split_Requires_Line_Total_To_Match_Loss()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 50m,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

        var result = await controller.RegisterDirectLoss(new ShipmentDirectLossCreateViewModel
        {
            ShipmentId = 1,
            EventDate = new DateTime(2026, 4, 22),
            LossQuantityMt = 5m,
            ResponsibilityType = ShipmentShortageResponsibilityTypes.Split,
            SplitLines =
            [
                new ShipmentDirectLossSplitLineInput
                {
                    ResponsibilityType = ShipmentShortageResponsibilityTypes.CompanyLoss,
                    QuantityMt = 2m
                }
            ]
        });

        Assert.IsType<RedirectResult>(result);
        Assert.Empty(await db.LossEvents.ToListAsync());
        Assert.True(controller.TempData.ContainsKey("error"));
    }

    [Fact]
    public async Task RegisterDirectLoss_Saves_Service_Claim_Without_Payment_Or_Ledger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 400m
        });
        db.ShipmentContracts.Add(new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 50m });
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Vessel,
            LoadedDate = new DateTime(2026, 4, 21),
            QuantityMt = 50m,
            Status = InventoryTransportLegStatus.Loaded
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider())
        };

        var result = await controller.RegisterDirectLoss(new ShipmentDirectLossCreateViewModel
        {
            ShipmentId = 1,
            EventDate = new DateTime(2026, 4, 22),
            LossQuantityMt = 5m,
            LossAmountUsd = 2000m,
            ResponsibilityType = ShipmentShortageResponsibilityTypes.ServiceProviderClaim,
            ResponsiblePartyName = "Transit Co",
            ClaimStatus = "در انتظار"
        });

        Assert.IsType<RedirectResult>(result);
        var loss = Assert.Single(await db.LossEvents.ToListAsync());
        Assert.Equal(ShipmentShortageResponsibilityTypes.ServiceProviderClaim, loss.ResponsiblePartyType);
        Assert.Equal("Transit Co", loss.ResponsiblePartyName);
        Assert.Contains("USD", loss.Notes);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Details_Groups_One_Shipment_Sale_Across_Two_Contracts_Without_Changing_Pnl_Or_Financial_Rows()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        AddTwoContractShipmentAllocations(db);
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 10,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            ShipmentId = 1,
            InvoiceNumber = "INV-SHIP-10",
            SaleDate = new DateTime(2026, 4, 23),
            QuantityMt = 50m,
            UnitPriceUsd = 100m,
            TotalUsd = 5000m
        });
        await db.SaveChangesAsync();

        var financialCountsBefore = new
        {
            Sales = await db.SalesTransactions.CountAsync(),
            Expenses = await db.ExpenseTransactions.CountAsync(),
            Losses = await db.LossEvents.CountAsync(),
            Payments = await db.PaymentTransactions.CountAsync(),
            Ledger = await db.LedgerEntries.CountAsync()
        };

        var controller = new ShipmentPnlController(db);
        var view = Assert.IsType<ViewResult>(await controller.Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var displaySale = Assert.Single(model.SaleDisplayRows);

        Assert.Single(model.Sales);
        Assert.Equal(50m, displaySale.QuantityMt);
        Assert.Equal(5000m, displaySale.TotalUsd);
        Assert.Equal(2, displaySale.ContractBreakdownLines.Count);
        Assert.Equal(50m, displaySale.ContractBreakdownLines.Sum(line => line.QuantityMt));
        Assert.Equal(5000m, displaySale.ContractBreakdownLines.Sum(line => line.AmountUsd));
        Assert.Equal(model.TotalSalesUsd, model.SaleDisplayRows.Sum(row => row.TotalUsd));
        Assert.Equal(
            model.TotalSalesUsd - model.TotalExpensesUsd,
            model.GrossMarginUsd);

        Assert.Equal(financialCountsBefore.Sales, await db.SalesTransactions.CountAsync());
        Assert.Equal(financialCountsBefore.Expenses, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(financialCountsBefore.Losses, await db.LossEvents.CountAsync());
        Assert.Equal(financialCountsBefore.Payments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(financialCountsBefore.Ledger, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Details_Groups_Allocated_Expenses_And_Losses_Into_One_Shipment_Row_Each()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        AddTwoContractShipmentAllocations(db);
        db.ExpenseTypes.Add(new ExpenseType
        {
            Id = 20,
            Code = "PORT-GROUP",
            Name = "Grouped port expense",
            NamePersian = "مصرف بندری"
        });
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 20,
                ExpenseTypeId = 20,
                ContractId = 2,
                ShipmentId = 1,
                TransportLegId = 100,
                ExpenseDate = new DateTime(2026, 4, 24),
                Amount = 60m,
                Currency = "USD",
                AmountUsd = 60m,
                Description = "Port service | GroupKey: SHIP-EXP-1 | Contract: PUR-001"
            },
            new ExpenseTransaction
            {
                Id = 21,
                ExpenseTypeId = 20,
                ContractId = 3,
                ShipmentId = 1,
                TransportLegId = 101,
                ExpenseDate = new DateTime(2026, 4, 24),
                Amount = 40m,
                Currency = "USD",
                AmountUsd = 40m,
                Description = "Port service | GroupKey: SHIP-EXP-1 | Contract: PUR-002"
            });
        db.LossEvents.AddRange(
            new LossEvent
            {
                Id = 20,
                ShipmentId = 1,
                ContractId = 2,
                TransportLegId = 100,
                ProductId = 1,
                Stage = LossEventStage.TransitLoss,
                EventDate = new DateTime(2026, 4, 25),
                ExpectedQuantityMt = 60m,
                ActualQuantityMt = 54m,
                DifferenceQuantityMt = 6m,
                ChargeableLossMt = 6m,
                Reference = "SHIPMENT-LOSS:GROUP-1",
                ResponsiblePartyType = ShipmentShortageResponsibilityTypes.CompanyLoss
            },
            new LossEvent
            {
                Id = 21,
                ShipmentId = 1,
                ContractId = 3,
                TransportLegId = 101,
                ProductId = 1,
                Stage = LossEventStage.TransitLoss,
                EventDate = new DateTime(2026, 4, 25),
                ExpectedQuantityMt = 40m,
                ActualQuantityMt = 36m,
                DifferenceQuantityMt = 4m,
                ChargeableLossMt = 4m,
                Reference = "SHIPMENT-LOSS:GROUP-1",
                ResponsiblePartyType = ShipmentShortageResponsibilityTypes.CompanyLoss
            });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);
        var view = Assert.IsType<ViewResult>(await controller.Details(1));
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);
        var displayExpense = Assert.Single(model.ExpenseDisplayRows);
        var displayLoss = Assert.Single(model.LossDisplayRows);

        Assert.Equal(2, model.Expenses.Count);
        Assert.Equal(100m, displayExpense.AmountUsd);
        Assert.Equal(2, displayExpense.ContractBreakdownLines.Count);
        Assert.Equal(model.TotalOperationalExpensesUsd, model.ExpenseDisplayRows.Sum(row => row.AmountUsd));

        Assert.Equal(2, model.Losses.Count);
        Assert.Equal(10m, displayLoss.QuantityMt);
        Assert.Equal(1400m, displayLoss.EstimatedValueUsd);
        Assert.Equal(2, displayLoss.ContractBreakdownLines.Count);
    }

    [Fact]
    public async Task Details_Includes_GroupTransfer_Truck_Customs_Sale_And_Expense()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "TRUCK-DISPATCH-FREIGHT", Name = "Truck freight", NamePersian = "کرایه موتر" });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });

        // واگنِ «حمل از موجودی» که ShipmentId والد را حفظ کرده است.
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 100,
            ShipmentId = 1,
            SourcePurchaseContractId = 2,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadedDate = new DateTime(2026, 5, 1),
            QuantityMt = 50m,
            Status = InventoryTransportLegStatus.InTransit
        });

        // رسید انتقال گروهی (واگن → موتر): DirectDispatch.
        db.InventoryTransportReceipts.Add(new InventoryTransportReceipt
        {
            Id = 500,
            InventoryTransportLegId = 100,
            ReceiptDate = new DateTime(2026, 5, 10),
            ReceivedQuantityMt = 25m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectDispatch
        });

        db.Trucks.Add(new Truck { Id = 1, PlateNumber = "KBL-111" });
        db.TruckDispatches.Add(new TruckDispatch
        {
            Id = 900,
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            InventoryTransportReceiptId = 500,
            ContractId = 2,
            ProductId = 1,
            TruckId = 1,
            DispatchDate = new DateTime(2026, 5, 10),
            Status = DispatchStatus.InTransit,
            LoadedQuantityMt = 25m,
            SalesTransactionId = 10
        });

        // فروش موتر (فروش دیسپچ؛ ShipmentId ندارد — فقط از راه دیسپچ ردیابی می‌شود).
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 10,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = "INV-TRUCK",
            SaleDate = new DateTime(2026, 5, 12),
            QuantityMt = 25m,
            UnitPriceUsd = 600m,
            TotalUsd = 15_000m
        });

        // گمرک موتر — فقط TruckDispatchId.
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 20,
            TruckDispatchId = 900,
            DeclarationDate = new DateTime(2026, 5, 11),
            TotalUsd = 300m
        });

        // مصرف موتر — فقط TruckDispatchId (مثل کرایه خودکار موتر).
        db.ExpenseTransactions.Add(new ExpenseTransaction
        {
            Id = 30,
            ExpenseTypeId = 1,
            TruckDispatchId = 900,
            ExpenseDate = new DateTime(2026, 5, 10),
            AmountUsd = 150m,
            Description = "Truck dispatch freight for dispatch #900"
        });
        await db.SaveChangesAsync();

        var controller = new ShipmentPnlController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShipmentPnlDetailsViewModel>(view.Model);

        // فروش موتر باید در فروش‌های پرونده کشتی بیاید.
        Assert.Contains(model.Sales, s => s.Id == 10);
        Assert.Equal(15_000m, model.TotalSalesUsd);

        // گمرک و مصرف موتر باید در مصارف عملیاتی پرونده کشتی جمع شوند.
        Assert.Contains(model.Expenses, e => e.ExpenseTypeName == "مصارف محصولی" && e.AmountUsd == 300m);
        Assert.Contains(model.Expenses, e => e.AmountUsd == 150m);
        Assert.Equal(450m, model.TotalOperationalExpensesUsd);
    }

    [Fact]
    public void Finance_Tab_Uses_Single_Whole_Shipment_Result_And_Full_Costs()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/ShipmentPnl/Details.cshtml");

        // نتیجهٔ واحد و واضح: کل فروش منهای قیمت کامل خرید تمام بار و همهٔ مصارف.
        Assert.Contains("سود کل محموله", view);
        Assert.Contains("زیان کل محموله", view);
        Assert.Contains("جمع‌بندی مالی محموله", view);
        Assert.Contains("منهای قیمت کامل خرید بار", view);
        Assert.Contains("منهای مصارف عملیاتی", view);
        Assert.Contains("Model.ShipmentNetResultUsd", view);
        Assert.Contains("Model.TotalPurchaseCostUsd", view);
        Assert.Contains("Model.TotalOperationalExpensesUsd", view);

        // جزئیات تکراری در تب‌های فروش و مصارف می‌مانند؛ تب مالی فقط خلاصهٔ مفید را نشان می‌دهد.
        Assert.DoesNotContain("جزئیات هزینه‌ها بر اساس دسته", view);
        Assert.DoesNotContain("سود ناخالص محقق‌شده", view);
        Assert.DoesNotContain("ارزش موجودی باقی‌مانده به بهای تمام‌شده", view);

        // سهم فروخته‌شده و ضرر شرکت نباید دوباره از نتیجهٔ کل محموله کم شوند.
        Assert.DoesNotContain("Model.RealizedPurchaseCostUsd", view);
        Assert.DoesNotContain("Model.RealizedOperationalExpensesUsd", view);
        Assert.DoesNotContain("Model.CompanyLossDeductionUsd", view);
        Assert.DoesNotContain("Model.RealizedGrossMarginUsd", view);
        // جدول per-leg همچنان نباید نتیجهٔ موازی بسازد.
        Assert.DoesNotContain("سود و زیان به تفکیک حمل", view);
        Assert.DoesNotContain("Model.GrossMarginUsd", view);
    }

    [Fact]
    public void ViewModel_Remaining_Inventory_Is_Tracked_But_Full_Purchase_Is_Deducted()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 40m,
            TotalSalesUsd = 6_000m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        Assert.Equal(60m, model.RemainingUnsoldQuantityMt);
        // میانگین بهای خرید = 10000/100 = 100 → ارزش موجودی باقی‌مانده = 60 × 100 = 6000.
        Assert.Equal(6_000m, model.RemainingInventoryValueUsd);
        // ارزش موجودی برای پیگیری باقی می‌ماند، ولی قیمت کامل خرید در نتیجه کسر می‌شود.
        Assert.Equal(1_600m, model.RealizedGrossMarginUsd);
        Assert.Equal(-5_000m, model.ShipmentNetResultUsd);
        Assert.False(model.IsFullySold);
    }

    [Fact]
    public void ViewModel_Unsold_Cargo_Is_Deducted_Without_Registering_Manual_Loss()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 0m,
            TotalSalesUsd = 0m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        // بدون فروش و بدون ثبت ضایعات: کل خرید و کل مصارف خودکار منفی می‌شوند.
        Assert.Equal(0m, model.RealizedPurchaseCostUsd);
        Assert.Equal(0m, model.RealizedOperationalExpensesUsd);
        Assert.Equal(0m, model.RealizedGrossMarginUsd);
        Assert.Equal(-11_000m, model.ShipmentNetResultUsd);
        Assert.False(model.NetResultIsProfit);
        // کلِّ ارزش بار به‌صورت موجودی جدا نگهداری می‌شود.
        Assert.Equal(10_000m, model.RemainingInventoryValueUsd);
    }

    [Fact]
    public void ViewModel_Recoverable_Shortage_Is_Not_Deducted_As_Company_Loss()
    {
        // کسری قابل‌وصول (کسر از تأمین‌کننده / طلب از خدماتی) CompanyLossQuantityMt نمی‌سازد،
        // پس نباید دوباره به‌عنوان زیان قطعی از سود کم شود.
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 40m,
            CompanyLossQuantityMt = 0m,
            TotalSalesUsd = 6_000m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        Assert.Equal(0m, model.CompanyLossDeductionUsd);
        // نتیجهٔ قدیمی تحقق‌یافته همچنان برای سازگاری داخلی 1600 است.
        Assert.Equal(1_600m, model.RealizedGrossMarginUsd);
        // نتیجهٔ رسمی به نوع کسری وابسته نیست؛ کل خرید قبلاً کامل کسر شده است.
        Assert.Equal(-5_000m, model.ShipmentNetResultUsd);
    }

    [Fact]
    public void ViewModel_Fully_Sold_Realized_Equals_Whole_Cargo_Margin()
    {
        var model = new ShipmentPnlDetailsViewModel
        {
            OriginalShipmentQuantityMt = 100m,
            ShipmentSalesQuantityMt = 100m,
            TotalSalesUsd = 15_000m,
            TotalPurchaseCostUsd = 10_000m,
            TotalOperationalExpensesUsd = 1_000m
        };

        Assert.True(model.IsFullySold);
        // وقتی همهٔ بار فروخته شد، بهای خرید و مصارفِ تحقق‌یافته با کلِّ محموله برابر می‌شوند،
        // پس سود تحقق‌یافته = درآمد − کل بهای خرید − کل مصارف (بدون دو عددِ متناقض).
        Assert.Equal(model.TotalPurchaseCostUsd, model.RealizedPurchaseCostUsd);
        Assert.Equal(model.TotalOperationalExpensesUsd, model.RealizedOperationalExpensesUsd);
        Assert.Equal(
            model.TotalSalesUsd - model.TotalPurchaseCostUsd - model.TotalOperationalExpensesUsd,
            model.RealizedGrossMarginUsd);
        Assert.Equal(4_000m, model.RealizedGrossMarginUsd);
        Assert.Equal(4_000m, model.ShipmentNetResultUsd);
    }

    private static void AddTwoContractShipmentAllocations(ApplicationDbContext db)
    {
        db.Contracts.AddRange(
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-001",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 60m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            },
            new Contract
            {
                Id = 3,
                ContractNumber = "PUR-002",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 20),
                QuantityMt = 40m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 200m
            });
        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = 1, ContractId = 2, QuantityMt = 60m },
            new ShipmentContract { ShipmentId = 1, ContractId = 3, QuantityMt = 40m });
        db.Terminals.Add(new Terminal { Id = 1, Code = "SRC", Name = "Source" });
        db.InventoryTransportLegs.AddRange(
            new InventoryTransportLeg
            {
                Id = 100,
                ShipmentId = 1,
                SourcePurchaseContractId = 2,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 60m,
                Status = InventoryTransportLegStatus.Loaded
            },
            new InventoryTransportLeg
            {
                Id = 101,
                ShipmentId = 1,
                SourcePurchaseContractId = 3,
                ProductId = 1,
                SourceTerminalId = 1,
                TransportType = LoadingTransportType.Vessel,
                LoadedDate = new DateTime(2026, 4, 21),
                QuantityMt = 40m,
                Status = InventoryTransportLegStatus.Loaded
            });
    }

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG Trading" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Locations.AddRange(
            new Location { Id = 1, Name = "Bandar Abbas" },
            new Location { Id = 2, Name = "Kabul Depot" });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "CON-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            ProductId = 1,
            CustomerId = 1,
            SupplierId = 1,
            DestinationLocationId = 2,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.Vessels.Add(new Vessel { Id = 1, Name = "MV Test" });
        db.Shipments.Add(new Shipment
        {
            Id = 1,
            ShipmentCode = "SHIP-01",
            VesselId = 1,
            ContractId = 1,
            DepartureDate = new DateTime(2026, 4, 21),
            ArrivalDate = new DateTime(2026, 4, 28),
            OriginLocationId = 1,
            DestinationLocationId = 2,
            QuantityMt = 50m,
            Notes = "Shipment note"
        });
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(RepoPath(relativePath));

    private static bool RepoFileExists(string relativePath)
        => File.Exists(RepoPath(relativePath));

    private static string RepoPath(string relativePath)
    {
        var baseDir = AppContext.BaseDirectory;
        var root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        return Path.Combine(root, relativePath);
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
