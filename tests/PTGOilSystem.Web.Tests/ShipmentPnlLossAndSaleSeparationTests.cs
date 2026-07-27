using PTGOilSystem.Web.Models.ShipmentPnl;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Regression tests for the shipment-file separation fixes:
///  1. «ضایعات» (recorded loss) must come only from real LossEvents; a dispatch-vs-receipt
///     transport shortage must never surface under the loss label (phantom-loss bug).
///  2. Direct-from-shipment sales and post-storage (tank/lineage) sales are shown separately
///     but each sale is counted exactly once in the attributed total.
/// These are pure view-model computations, so they run without a database.
/// </summary>
public class ShipmentPnlLossAndSaleSeparationTests
{
    private static ShipmentContractLineViewModel ContractLine(
        int contractId,
        decimal transportShortageMt)
        => new()
        {
            ContractId = contractId,
            ContractNumber = $"C-{contractId}",
            TransportShortageQuantityMt = transportShortageMt
        };

    private static ShipmentPnlSalesItemViewModel Sale(
        int id,
        bool isDirect,
        decimal quantityMt,
        decimal totalUsd)
        => new()
        {
            Id = id,
            InvoiceNumber = $"INV-{id}",
            QuantityMt = quantityMt,
            TotalUsd = totalUsd,
            UnitPriceUsd = quantityMt > 0m ? totalUsd / quantityMt : 0m,
            IsDirectShipmentSale = isDirect
        };

    [Fact]
    public void Transport_Shortage_Without_LossEvent_Is_Not_Counted_As_Recorded_Loss()
    {
        // A root leg dispatched more than was received (silent gap), but no LossEvent exists.
        var vm = new ShipmentPnlDetailsViewModel
        {
            ContractLines = [ContractLine(contractId: 1, transportShortageMt: 5m)],
            InventoryTransportShortageQuantityMt = 2m,
            DirectLossQuantityMt = 0m,
            LossQuantityMt = 0m
        };

        // ضایعات ثبت‌شده = صفر چون هیچ LossEvent ثبت نشده.
        Assert.Equal(0m, vm.RecordedLossQuantityMt);
        // کسری از اختلاف بارگیری/رسید می‌آید و جدا شمرده می‌شود.
        Assert.Equal(7m, vm.TotalTransportShortageMt);
    }

    [Fact]
    public void Recorded_Loss_Reflects_Real_LossEvents_Only()
    {
        var vm = new ShipmentPnlDetailsViewModel
        {
            ContractLines = [ContractLine(contractId: 1, transportShortageMt: 5m)],
            InventoryTransportShortageQuantityMt = 0m,
            DirectLossQuantityMt = 3m,
            LossQuantityMt = 3m
        };

        Assert.Equal(3m, vm.RecordedLossQuantityMt);
        Assert.Equal(5m, vm.TotalTransportShortageMt);
    }

    [Fact]
    public void Direct_And_Storage_Sales_Split_But_Sum_Once()
    {
        var vm = new ShipmentPnlDetailsViewModel
        {
            Sales =
            [
                Sale(id: 1, isDirect: true, quantityMt: 10m, totalUsd: 5000m),
                Sale(id: 2, isDirect: false, quantityMt: 4m, totalUsd: 2000m)
            ]
        };

        Assert.Equal(10m, vm.DirectSaleQuantityMt);
        Assert.Equal(5000m, vm.DirectSaleUsd);
        Assert.Equal(4m, vm.StorageSaleQuantityMt);
        Assert.Equal(2000m, vm.StorageSaleUsd);
        Assert.True(vm.HasDirectShipmentSales);
        Assert.True(vm.HasStorageOriginSales);
        // جمع دو گروه = کل، بدون دوبار شمردن.
        Assert.Equal(14m, vm.DirectSaleQuantityMt + vm.StorageSaleQuantityMt);
        Assert.Equal(7000m, vm.DirectSaleUsd + vm.StorageSaleUsd);
    }

    [Fact]
    public void Sale_Display_Rows_Carry_Origin()
    {
        var vm = new ShipmentPnlDetailsViewModel
        {
            Sales =
            [
                Sale(id: 1, isDirect: true, quantityMt: 10m, totalUsd: 5000m),
                Sale(id: 2, isDirect: false, quantityMt: 4m, totalUsd: 2000m)
            ]
        };

        var direct = Assert.Single(vm.SaleDisplayRows, r => r.InvoiceNumber == "INV-1");
        var storage = Assert.Single(vm.SaleDisplayRows, r => r.InvoiceNumber == "INV-2");
        Assert.True(direct.IsDirectShipmentSale);
        Assert.False(storage.IsDirectShipmentSale);
    }
}
