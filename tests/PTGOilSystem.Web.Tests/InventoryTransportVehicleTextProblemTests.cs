using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// متنِ ردیف‌های وسایط (به‌خصوص از اکسل) پیش از دیتابیس سنجیده می‌شود تا کاربر به‌جای پیام کلیِ
/// «قواعد دیتابیس» بداند کدام ردیف و کدام خانه مشکل دارد و چگونه اصلاحش کند.
/// </summary>
public class InventoryTransportVehicleTextProblemTests
{
    [Fact]
    public void Clean_Row_Has_No_Problem()
    {
        var model = ModelWith(new InventoryTransportVehicleInput
        {
            TransportType = LoadingTransportType.Truck,
            TruckPlateNumberInput = "KBL-1234",
            DriverNameInput = "عبدالله‌جان",
            RwbNo = "CMR-100"
        });

        Assert.Empty(InventoryTransportBatchService.FindVehicleTextProblems(model));
    }

    [Fact]
    public void Invisible_Character_Names_Row_Field_And_Fix()
    {
        var model = ModelWith(
            new InventoryTransportVehicleInput { TruckPlateNumberInput = "KBL-1" },
            new InventoryTransportVehicleInput { RwbNo = "CMR-\0-200" });

        var problem = Assert.Single(InventoryTransportBatchService.FindVehicleTextProblems(model));

        Assert.Equal("Vehicles[1].RwbNo", problem.FieldKey);
        Assert.Contains("ردیف 2", problem.Message);
        Assert.Contains("سیمیر/CMR", problem.Message);
        Assert.Contains("کاراکتر نامرئی", problem.Message);
        Assert.Contains("CLEAN()", problem.Message);
    }

    [Fact]
    public void Too_Long_Plate_Reports_Length_And_Limit()
    {
        var model = ModelWith(new InventoryTransportVehicleInput { TruckPlateNumberInput = new string('A', 61) });

        var problem = Assert.Single(InventoryTransportBatchService.FindVehicleTextProblems(model));

        Assert.Equal("Vehicles[0].TruckPlateNumberInput", problem.FieldKey);
        Assert.Contains("ردیف 1", problem.Message);
        Assert.Contains("61 حرف", problem.Message);
        Assert.Contains("50 حرف", problem.Message);
    }

    [Fact]
    public void Notes_Allow_New_Lines_But_Not_Nul()
    {
        var withNewLines = ModelWith();
        withNewLines.Notes = "خط اول\r\nخط دوم";
        Assert.Empty(InventoryTransportBatchService.FindVehicleTextProblems(withNewLines));

        var withNul = ModelWith();
        withNul.Notes = "یادداشت\0";
        var problem = Assert.Single(InventoryTransportBatchService.FindVehicleTextProblems(withNul));
        Assert.Equal("Notes", problem.FieldKey);
    }

    [Fact]
    public void Excel_Text_Cleaning_Removes_Control_Characters_And_Keeps_Zwnj()
    {
        Assert.Equal("KBL 1234", InventoryTransportVehicleWorkbookParser.CleanText("KBL\0\t 1234\r\n"));
        Assert.Equal("عبدالله‌جان", InventoryTransportVehicleWorkbookParser.CleanText("عبدالله‌جان"));
        Assert.Equal(string.Empty, InventoryTransportVehicleWorkbookParser.CleanText(null));
    }

    private static InventoryTransportFromInventoryViewModel ModelWith(params InventoryTransportVehicleInput[] vehicles)
        => new() { Vehicles = vehicles.ToList() };
}
