using System;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// این تست‌ها ساختِ استخراج‌شدهٔ ردیفِ لجرِ فروش را قفل می‌کنند تا مطمئن شویم
// همان مقادیرِ قبلی (پیش از یکی‌شدنِ ۵ سایت) تولید می‌شود.
public class SaleLedgerFactoryTests
{
    private static SalesTransaction SampleSale() => new()
    {
        Id = 42,
        SaleStage = SaleStage.InTransit,
        InvoiceNumber = "INV-1001",
        SaleDate = new DateTime(2026, 3, 15),
        TotalUsd = 1234.5678m,
        TotalInCurrency = 87654.321m,
        Currency = "AFN",
        AppliedFxRateToUsd = 71m,
        CustomerId = 7,
        ShipmentId = 3
    };

    [Fact]
    public void BuildSaleLedgerEntry_Maps_All_Fields_From_Sale_And_Conversion()
    {
        var sale = SampleSale();
        var conversion = new CurrencyConversionResult(
            SourceCurrencyCode: "AFN",
            BaseCurrencyCode: SystemCurrency.BaseCurrencyCode,
            AppliedRateToBase: 71m,
            EffectiveDate: new DateTime(2026, 3, 14),
            FallbackApplied: false,
            ManualOverride: false,
            SourceDescription: "Daily FX 2026-03-14");

        var entry = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: 55);

        Assert.Equal(sale.SaleDate, entry.EntryDate);
        Assert.Equal(LedgerSide.Credit, entry.Side);
        Assert.Equal(sale.TotalUsd, entry.AmountUsd);
        Assert.Equal(SystemCurrency.BaseCurrencyCode, entry.Currency);
        Assert.Equal(sale.TotalInCurrency, entry.SourceAmount);
        Assert.Equal(sale.Currency, entry.SourceCurrencyCode);
        Assert.Equal(sale.AppliedFxRateToUsd, entry.AppliedFxRateToUsd);
        Assert.Equal(conversion.EffectiveDate.Date, entry.AppliedFxRateDate);
        Assert.Equal(conversion.SourceDescription, entry.AppliedFxRateSource);
        Assert.Equal("Sale", entry.SourceType);
        Assert.Equal(sale.Id, entry.SourceId);
        Assert.Equal(sale.InvoiceNumber, entry.Reference);
        Assert.Equal(55, entry.ContractId);
        Assert.Equal(sale.CustomerId, entry.CustomerId);
        Assert.Equal(sale.ShipmentId, entry.ShipmentId);
        Assert.Equal(SaleLedgerFactory.BuildDescription(sale), entry.Description);
    }

    [Fact]
    public void BuildSaleLedgerEntry_Allows_Null_ContractId()
    {
        var sale = SampleSale();
        var conversion = new CurrencyConversionResult(
            "AFN", SystemCurrency.BaseCurrencyCode, 71m, new DateTime(2026, 3, 14), false, false, "x");

        var entry = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: null);

        Assert.Null(entry.ContractId);
    }

    [Fact]
    public void BuildSaleLedgerEntry_For_Supplier_Uses_Only_Supplier_Party()
    {
        var sale = SampleSale();
        sale.CustomerId = null;
        sale.SupplierId = 19;
        var conversion = new CurrencyConversionResult(
            "AFN", SystemCurrency.BaseCurrencyCode, 71m, new DateTime(2026, 3, 14), false, false, "x");

        var entry = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: null);

        Assert.Null(entry.CustomerId);
        Assert.Equal(19, entry.SupplierId);
        Assert.Equal(LedgerSide.Credit, entry.Side);
        Assert.Equal("Sale", entry.SourceType);
    }

    [Fact]
    public void BuildDescription_Matches_Legacy_Format()
    {
        var sale = SampleSale();
        Assert.Equal(
            $"ثبت فروش {SaleStageLabels.ToPersian(sale.SaleStage)} فاکتور {sale.InvoiceNumber}",
            SaleLedgerFactory.BuildDescription(sale));
    }
}
