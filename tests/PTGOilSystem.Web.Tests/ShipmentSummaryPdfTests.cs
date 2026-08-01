using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Services.Exports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class ShipmentSummaryPdfTests
{
    [Fact]
    public void Summary_Pdf_Model_Uses_The_Existing_Shipment_Details_Values()
    {
        var model = ShipmentPnlController.BuildShipmentSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal("MV SADIQI", model.VesselName);
        Assert.Equal(ShipmentSummaryPdfTone.Warning, model.StatusTone);
        Assert.Equal(6, model.Stages.Count);
        Assert.All(model.Stages, stage => Assert.Equal(3, stage.Metrics.Count));

        var voyage = model.Stages[0];
        Assert.Contains(voyage.Metrics, metric => metric.Value == "100.000" && metric.Unit == "تن");
        Assert.Contains(voyage.Metrics, metric => metric.Value == "2026/07/01");
        Assert.Contains(voyage.Metrics, metric => metric.Value == "2026/07/12");

        var unloading = model.Stages[1];
        Assert.Contains(unloading.Metrics, metric => metric.Label == "تخلیه‌شده در بندر مبدأ" && metric.Value == "90.000");
        Assert.Contains(unloading.Metrics, metric => metric.Label == "فروش مستقیم از داخل کشتی" && metric.Value == "20.000");
        Assert.Contains(unloading.Metrics, metric => metric.Value == "1.000" && metric.Tone == ShipmentSummaryPdfTone.Negative);

        var transport = model.Stages[2];
        Assert.Equal("انتقال بندر مبدأ به ترمینال مقصد", transport.Title);
        Assert.Contains(transport.Metrics, metric => metric.Label == "ارسال‌شده از بندر مبدأ" && metric.Value == "70.000");
        Assert.Contains(transport.Metrics, metric => metric.Label == "در راه به ترمینال مقصد" && metric.Value == "4.000");
        Assert.Contains(transport.Metrics, metric => metric.Label == "تحویل‌شده در ترمینال مقصد" && metric.Value == "65.000");

        var sales = model.Stages[3];
        Assert.Equal("38.000 تن فروش‌نشده", sales.StatusText);
        Assert.Contains(sales.Metrics, metric => metric.Label == "فروش مستقیم از داخل کشتی" && metric.Value == "20.000");
        Assert.Contains(sales.Metrics, metric => metric.Label == "فروش پس از تخلیه در بندر مبدأ" && metric.Value == "40.000");
        Assert.Contains(sales.Metrics, metric => metric.Label == "کل مقدار فروخته‌شده" && metric.Value == "60.000");

        var finalResult = model.Stages[5];
        Assert.Contains(finalResult.Metrics, metric => metric.Value == "2,000.00" && metric.Unit == "USD");
        Assert.Contains(model.Stages[4].Metrics, metric => metric.Value == "62,000.00" && metric.Unit == "USD");
    }

    [Fact]
    public void Summary_Pdf_Uses_The_Shared_Office_Layout_Without_Decorative_Icons()
    {
        var webRoot = FindWebRoot();
        var source = File.ReadAllText(Path.Combine(
            Directory.GetParent(webRoot)!.FullName,
            "Services",
            "Exports",
            "ShipmentSummaryPdfDocument.cs"));

        Assert.Contains("PdfDesignSystem.PersianFallbackFont", source);
        Assert.Contains("PdfDesignSystem.HeaderCell", source);
        Assert.Contains("PdfDesignSystem.BodyCell", source);
        Assert.Contains("PdfDesignSystem.TableSeparator", source);
        Assert.DoesNotContain(".Svg(", source);
        Assert.DoesNotContain("ComposeShipIcon", source);
        Assert.DoesNotContain("CornerRadius", source);
        Assert.DoesNotContain("ToneSurface", source);
    }

    [Fact]
    public async Task Summary_Pdf_Is_A_Valid_Graphical_Document()
    {
        var service = CreateService();
        var pdfModel = ShipmentPnlController.BuildShipmentSummaryPdfModel(
            BuildDetails(),
            isEnglish: false,
            generatedAt: new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        await using var stream = new MemoryStream();
        await service.WriteShipmentSummaryPdfAsync(pdfModel, isEnglish: false, stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 1_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        var sampleDirectory = Environment.GetEnvironmentVariable("PTG_SHIPMENT_PDF_SAMPLE_DIR");
        if (!string.IsNullOrWhiteSpace(sampleDirectory))
        {
            Directory.CreateDirectory(sampleDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(sampleDirectory, "shipment-summary-sample.pdf"),
                bytes);
        }
    }

    private static ShipmentPnlDetailsViewModel BuildDetails()
        => new()
        {
            Id = 41,
            ShipmentCode = "SHP-2026-041",
            VesselName = "MV SADIQI",
            CompanyName = "گروه کمپنی‌های فواد صدیقی",
            ProductName = "دیزل",
            ContractNumber = "PUR-41",
            SupplierName = "تأمین‌کننده نمونه",
            CustomerName = "مشتری نمونه",
            OriginName = "بندر مبدأ",
            DestinationName = "ترمینال مقصد",
            DepartureDate = new DateTime(2026, 7, 1),
            ArrivalDate = new DateTime(2026, 7, 12),
            OriginalShipmentQuantityMt = 100m,
            VesselUnloadedQuantityMt = 90m,
            InventoryTransportedOutQuantityMt = 70m,
            DeliveredAtDestinationQuantityMt = 65m,
            InTransitQuantityMt = 4m,
            RemainingInSourceTankQuantityMt = 20m,
            InventoryTransportShortageQuantityMt = 1m,
            ShipmentSalesQuantityMt = 60m,
            LossQuantityMt = 0m,
            TotalSalesUsd = 62_000m,
            TotalPurchaseCostUsd = 55_000m,
            TotalOperationalExpensesUsd = 5_000m,
            CustomerReceiptsUsd = 50_000m,
            Notes = "این یادداشت فقط از پروندهٔ محموله آمده است.",
            NeedsReviewCount = 1,
            Warnings = ["یک انتقال داخلی هنوز در مسیر است."],
            ContractLines =
            [
                new ShipmentContractLineViewModel
                {
                    ContractId = 41,
                    ContractNumber = "PUR-41",
                    SupplierName = "تأمین‌کننده نمونه",
                    AllocatedQuantityMt = 100m,
                    UsedQuantityMt = 100m,
                    TransportShortageQuantityMt = 1m,
                    HasFinalPrice = true,
                    UnitPriceUsd = 550m,
                    TotalValueUsd = 55_000m
                }
            ],
            RegisteredVesselReceipts =
            [
                new ShipmentPnlRegisteredVesselReceiptItemViewModel
                {
                    Id = 1,
                    ReceiptDate = new DateTime(2026, 7, 12),
                    ContractNumber = "PUR-41",
                    DestinationTerminalName = "ترمینال مقصد",
                    DestinationTankName = "مخزن 1",
                    ReceivedQuantityMt = 89m
                }
            ],
            TransportLegs =
            [
                new ShipmentPnlTransportLegItemViewModel
                {
                    Id = 2,
                    LoadedDate = new DateTime(2026, 7, 15),
                    SourceIsStorageTank = true,
                    SourceName = "مخزن 1",
                    DestinationName = "انبار مقصد",
                    QuantityMt = 70m,
                    ReceivedQuantityMt = 65m,
                    ShortageQuantityMt = 1m
                }
            ],
            Sales =
            [
                new ShipmentPnlSalesItemViewModel
                {
                    Id = 8,
                    SaleDate = new DateTime(2026, 7, 20),
                    InvoiceNumber = "INV-8",
                    CustomerName = "مشتری نمونه",
                    QuantityMt = 20m,
                    TotalUsd = 20_000m,
                    IsDirectShipmentSale = true
                },
                new ShipmentPnlSalesItemViewModel
                {
                    Id = 9,
                    SaleDate = new DateTime(2026, 7, 21),
                    InvoiceNumber = "INV-9",
                    CustomerName = "مشتری نمونه",
                    QuantityMt = 40m,
                    TotalUsd = 42_000m,
                    IsDirectShipmentSale = false
                }
            ],
            CustomerReceipts =
            [
                new ShipmentPnlCustomerReceiptItemViewModel
                {
                    Id = 9,
                    PaymentDate = new DateTime(2026, 7, 25),
                    CustomerName = "مشتری نمونه",
                    AmountUsd = 50_000m,
                    IsInflow = true
                }
            ]
        };

    private static TabularExportService CreateService()
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        var options = Options.Create(new TabularExportOptions
        {
            ExcelMaxRows = 100,
            PdfMaxRows = 100,
            CompanyNameFa = "گروه کمپنی‌های فواد صدیقی",
            CompanyNameEn = "Fawad Saddiqi Group of Companies",
            CompanyLogoPath = "/images/logo1-sidebar.png",
            CompanyPhone = "+93 000 000 000",
            CompanyEmail = "hidden@example.com",
            CompanyWebsite = "hidden.example.com",
            QuestPdfLicense = "Community"
        });
        return new TabularExportService(options, environment);
    }

    private static string FindWebRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "PTGOilSystem.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
