using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class TransportWorkflowViewStructureTests
{
    [Fact]
    public void Transport_Index_Is_The_Single_Compact_Operational_List()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Index.cshtml");

        Assert.Contains("حمل‌ها", view);
        Assert.Contains("+ ثبت حمل", view);
        Assert.Contains("Filter.WorkflowState", view);
        Assert.Contains("Filter.TransportType", view);
        Assert.Contains("Filter.ProductId", view);
        Assert.Contains("Filter.ContractId", view);
        Assert.Contains("Filter.StorageTankId", view);
        Assert.Contains("Filter.FromDate", view);
        Assert.Contains("Filter.ToDate", view);
        // ستون‌های مقدار، وسیله و وضعیت عمداً تک‌خطی هستند؛ باقی‌مانده و موقعیت فعلی از لیست حذف شده‌اند.
        Assert.Contains("NumberDisplay.Quantity(item.QuantityMt)", view);
        Assert.DoesNotContain("RemainingQuantityMt", view);
        Assert.DoesNotContain("CurrentLocationLabel", view);
        Assert.DoesNotContain("transport-kpi", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_Transport_Offers_Only_The_Three_User_Source_Choices()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/Transports/Create.cshtml");

        Assert.Contains("موجودی مخزن", view);
        Assert.Contains("رسید/بارگیری مستقیم", view);
        Assert.Contains("حمل در جریان", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("ثبت حمل", view);
        Assert.DoesNotContain("Dispatch", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Journey", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Details_Centres_The_Chain_And_Valid_Next_Actions()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");

        Assert.Contains("transport-chain-timeline", view);
        Assert.Contains("ParentLegIds", view);
        Assert.Contains("انتقال به وسیله دیگر", view);
        // The unload action is named after the operator's outcome ("deliver at destination"),
        // and freight has exactly one label everywhere on the page.
        Assert.Contains("تحویل در مقصد", view);
        Assert.Contains("ثبت کسری مستقل (بدون تخلیه)", view);
        Assert.Contains("ثبت گمرک", view);
        Assert.Contains("تسویه کرایه", view);
        Assert.Contains("ثبت وزن، کسری و کرایه", view);
        Assert.Contains("فروش مستقیم بار", view);
        Assert.DoesNotContain("تسویه کامل موتر", view);
        Assert.Contains("canSimpleFreightSettlement", view);
        Assert.Contains("LoadingTransportType.Truck or LoadingTransportType.Wagon", view);
        Assert.DoesNotContain("تسویهٔ کرایه", view);
        Assert.DoesNotContain("ثبت سادهٔ کرایه", view);
        Assert.Contains("لغو / برگشت", view);
        Assert.Contains("asp-controller=\"Transports\" asp-action=\"SettleFreight\"", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("ConfirmMessage", view);
    }

    [Fact]
    public void Full_Truck_Settlement_Is_Reachable_And_Remains_One_Existing_Engine()
    {
        var tabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");
        var index = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Index.cshtml");
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/InventoryTransportLegs/Details.cshtml");
        var settlement = ReadRepoFile("src/PTGOilSystem.Web/Views/TruckSettlements/Index.cshtml");

        Assert.Contains("\"TruckSettlements\"", tabs);
        Assert.Contains("\"Truck Settlements\"", tabs);
        Assert.DoesNotContain("TruckSettlements", index);
        Assert.Contains("Url.Action(\"Index\", \"TruckSettlements\"", details);
        Assert.Contains("kind = TruckSettlementSourceKind.Leg", details);
        Assert.Contains("sourceId = Model.Id", details);
        Assert.Contains("Model.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit", details);
        Assert.Contains("asp-controller=\"Transports\" asp-action=\"SettleFreight\"", details);
        Assert.Contains("ثبت وزن، کسری و کرایه", details);

        Assert.Contains("data-operations-list", settlement);
        Assert.Contains("_ExcelImport", settlement);
        Assert.Contains("id=\"tsSelectionSummary\"", settlement);
        Assert.Contains("@Html.AntiForgeryToken()", settlement);
        Assert.Contains("remaining - qty", settlement);
        Assert.Contains("Math.max(freight - deductions, 0) - (chargeable * shortageRate)", settlement);
    }

    [Fact]
    public void Legacy_Entry_Points_Redirect_And_Cannot_Write_A_Parallel_Transfer()
    {
        var receiptController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportReceiptsController.cs");
        var dispatchController = Normalize(ReadRepoFile("src/PTGOilSystem.Web/Controllers/DispatchController.cs"));
        var legController = Normalize(ReadRepoFile("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs"));

        Assert.Contains("RedirectToAction(\"Continue\", \"Transports\"", receiptController);
        Assert.Contains("\"Create\", \"Transports\"", dispatchController);
        Assert.Contains("\"Create\", \"Transports\"", legController);
    }

    [Fact]
    public void New_Posts_Are_Permission_And_AntiForgery_Protected()
    {
        var controller = ReadRepoFile("src/PTGOilSystem.Web/Controllers/TransportsController.cs");
        var roleRules = ReadRepoFile("src/PTGOilSystem.Web/Security/RoleAccessRules.cs");

        Assert.Contains("[Authorize(Policy = AuthPolicies.ManageData)]", controller);
        Assert.Equal(4, Count(controller, "[ValidateAntiForgeryToken]"));
        Assert.Contains("Transports", roleRules);
    }

    [Fact]
    public void Inventory_Writes_Are_Centralized_In_The_Writer()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "PTGOilSystem.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Controllers{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}"))
            .Where(path => !path.EndsWith("InventoryMovementWriter.cs", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("_db.InventoryMovements.Add(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.InventoryMovements.AddRange(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.InventoryMovements.Remove(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.InventoryMovements.RemoveRange(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.InventoryMovements.Update(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.InventoryMovements.UpdateRange(", source, StringComparison.Ordinal);
        }
    }

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string Normalize(string source)
        => System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ");

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    private static string RepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
