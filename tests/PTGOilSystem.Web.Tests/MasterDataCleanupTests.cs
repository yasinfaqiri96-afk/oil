using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class MasterDataCleanupTests
{
    [Fact]
    public void Master_Data_Entities_Expose_Only_Safe_Descriptive_Fields()
    {
        AssertStringProperty<Product>("Category", 150);
        AssertStringProperty<Product>("Notes", 1000);

        AssertStringProperty<Location>("Code", 50);
        AssertStringProperty<Location>("Notes", 1000);
        AssertTrueByDefault<Location>("IsActive");

        AssertStringProperty<ExpenseType>("Notes", 1000);

        AssertStringProperty<Partner>("Address", 300);
        AssertStringProperty<Partner>("Email", 150);

        AssertStringProperty<Company>("Address", 300);
        AssertStringProperty<Company>("Notes", 1000);

        AssertStringProperty<Supplier>("Code", 50);
        AssertStringProperty<Supplier>("Address", 300);
        AssertStringProperty<Supplier>("Notes", 1000);

        AssertStringProperty<Customer>("Code", 50);
        AssertStringProperty<Customer>("Address", 300);
        AssertStringProperty<Customer>("Notes", 1000);

        AssertStringProperty<Terminal>("Notes", 1000);

        AssertStringProperty<StorageTank>("DisplayName", 150);
        AssertStringProperty<StorageTank>("Notes", 1000);
        AssertTrueByDefault<StorageTank>("IsActive");

        AssertStringProperty<Truck>("Notes", 1000);

        AssertStringProperty<Wagon>("WagonType", 50);
        AssertStringProperty<Wagon>("Owner", 100);
        AssertStringProperty<Wagon>("Notes", 1000);
        AssertTrueByDefault<Wagon>("IsActive");

        AssertStringProperty<Driver>("NationalId", 50);
        AssertStringProperty<Driver>("Address", 300);
        AssertStringProperty<Driver>("Notes", 1000);

        AssertStringProperty<Vessel>("Code", 50);
        AssertStringProperty<Vessel>("OwnerOrOperator", 150);
        AssertStringProperty<Vessel>("Notes", 1000);

        AssertStringProperty<CashAccount>("AccountNumber", 50);
        AssertStringProperty<CashAccount>("BankName", 150);
        AssertStringProperty<CashAccount>("Branch", 150);
    }

    [Fact]
    public void Sidebar_Exposes_Primary_Items_And_Goods_Logistics_Group()
    {
        // The original test was written when the sidebar still exposed a
        // "Base Settings" group. The current layout has been refactored to a
        // flat list of primary sidebar items plus a single Goods & Logistics
        // group, so the assertions are aligned with the new structure. The
        // intent is unchanged: pin down the sidebar wiring so that future
        // refactors cannot silently drop entries.
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var sectionTabs = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_SectionTabs.cshtml");

        // Foundation/operational source-of-truth used to build coreData controllers.
        Assert.Contains("var coreDataItems = foundationItems.Select", layout);
        Assert.Contains(".Concat(operationalResourceItems", layout);
        Assert.Contains("var coreDataControllers = coreDataItems.Select(item => item.Controller).ToArray();", layout);
        Assert.Contains("var businessPartyGroupControllers = businessPartyItems.Select(item => item.Controller).ToArray();", layout);
        // پشتیبان‌گیری و بازیابی هم زیر همین گروه مدیریت می‌نشینند.
        Assert.Contains("var adminManagementControllers = new[] { \"Users\", \"Roles\", \"AuditLogs\", \"Backups\", \"BackupRestore\" };", layout);
        Assert.Contains("(\"Backups\", \"Index\"", layout);

        // The deprecated "base-settings" / "Core Data & Settings" sidebar
        // group must NOT be re-introduced.
        Assert.DoesNotContain("var baseSettingsItems", layout);
        Assert.DoesNotContain("(\"base-settings\"", layout);
        Assert.DoesNotContain("Core Data & Settings", layout);
        Assert.DoesNotContain("(\"core-data\"", layout);

        // Two-rail Akaunting navigation model (navSource -> navTree).
        Assert.Contains("var navSource = new List<", layout);
        Assert.Contains("NavNode(\"Home\", \"Index\"", layout);
        Assert.Contains("NavNode(\"Contracts\", \"Index\"", layout);
        Assert.Contains("NavNode(\"Loading\", \"Index\"", layout);
        Assert.Contains("\"Operations\")", layout);
        Assert.Contains("NavNode(\"Finance\", \"Index\"", layout);
        Assert.Contains("NavNode(\"StorageTanks\", \"Index\"", layout);

        // «گزارش‌ها» یک آیتم تکی به هاب Reports/Index است و زیرمنو ندارد؛ هر گزارش
        // (از جمله تفاوت نرخ ارز) از کارت‌های همان هاب باز می‌شود.
        Assert.Contains("NavNode(\"Reports\", \"Index\"", layout);
        Assert.DoesNotContain("children: reportMenuItems", layout);

        // Expandable submenus wired from the existing child sources.
        Assert.Contains("children: businessPartyItems", layout);
        Assert.Contains("children: transportItems", layout);
        Assert.Contains("children: baseDefinitionChildren", layout);
        Assert.Contains("(\"ServiceProviders\", \"Index\"", layout);
        Assert.Contains("(\"ServiceProviders\", \"Index\"", sectionTabs);

        // Compact menu panel with inline accordion submenus.
        Assert.Contains("boltz-sidebar", layout);
        Assert.Contains("ak-navpanel", layout);
        Assert.Contains("data-nav-group", layout);
        Assert.Contains("data-nav-group-toggle=\"true\"", layout);
        Assert.Contains("boltz-nav-sub", layout);
        Assert.Contains("NavChildIsActive", layout);

        // The deprecated grouped-submenu / base-settings source must not return.
        Assert.DoesNotContain("var goodsLogisticsItems", layout);
        Assert.DoesNotContain("(\"goods-logistics\"", layout);
        Assert.DoesNotContain("Goods Operations & Logistics", layout);
        Assert.DoesNotContain("boltz-nav-group-panel", layout);
        Assert.DoesNotContain("IsSidebarGroupActive", layout);
        // Foundation tabs in the section-tabs partial.
        Assert.Contains("(\"Products\"", layout);
        Assert.Contains("(\"Terminals\"", layout);
        Assert.Contains("(\"Wagons\"", layout);
        Assert.Contains("\"CashAccounts\"", layout);
        Assert.Contains("var foundationTabs = new (string Controller, string Action, string Label, string Icon, string? RouteKey)[]", sectionTabs);
        Assert.Contains("var transportTabs = new (string Controller, string Action, string Label, string Icon, string? RouteKey)[]", sectionTabs);
        Assert.Contains("tabs = foundationTabs;", sectionTabs);
        Assert.Contains("(\"InventoryTransportLegs\", \"Index\", T(\"حمل موجودی\", \"Inventory Transfer\")", sectionTabs);
        Assert.Contains("(\"Locations\",    \"Index\", T(\"بنادر\",           \"Ports\")", sectionTabs);
        Assert.DoesNotContain("(\"Locations\",    \"Index\", T(\"مکان‌ها\",       \"Locations\")", sectionTabs);
    }

    [Fact]
    public void ServiceProvider_Profile_Uses_Simplified_Three_Tab_Layout()
    {
        // پروفایل شرکت خدماتی به سه‌تب سادهٔ طرف‌حساب بازطراحی شده است
        // (خلاصه حساب / صورت‌حساب / مصارف و پرداخت‌ها) و دیگر به متریک‌کارت‌های
        // قدیمی تأمین‌کننده یا فایل حذف‌شدهٔ view-structure.css وابسته نیست.
        var details = ReadRepoFile("src/PTGOilSystem.Web/Views/ServiceProviders/Details.cshtml");

        Assert.Contains("service-provider-details-page", details);

        // ترکیب مشترک جزئیات طرف‌حساب (نسخهٔ ۲): خلاصه همیشه دیده می‌شود و تب‌های
        // بالای صفحه پنهان‌اند؛ صورت‌حساب و اسناد دو بخش قابل‌نمایش با شناسهٔ خودشان‌اند.
        Assert.Contains("data-ak-detail-v2=\"true\"", details);
        Assert.Contains("ViewData[\"HideSectionTabs\"] = true", details);
        Assert.Contains("id=\"provider-statement\"", details);
        Assert.Contains("id=\"provider-documents\"", details);

        // اکشن‌های واقعی موجود در سیستم (پرداخت و ثبت مصرف برای همین شرکت خدماتی).
        Assert.Contains("\"Create\", \"Payments\"", details);
        Assert.Contains("\"Create\", \"Expenses\"", details);
        Assert.Contains("serviceProviderId = Model.Id", details);

        // صورت‌حساب رسمی به‌صورت درون‌خطی از PartyStatements بارگذاری می‌شود، نه لینک ساده.
        Assert.Contains("ak-party-statement-embed", details);
        Assert.Contains("Url.Action(\"ServiceProvider\", \"PartyStatements\"", details);

        // متریک‌کارت‌های شلوغ قدیمی دیگر استفاده نمی‌شوند.
        Assert.DoesNotContain("supplier-journal-shot-grid", details);
    }

    [Fact]
    public void Counterparty_Details_Use_The_Shared_Compact_Composition()
    {
        var layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
        var tabsPartial = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/Partials/_DetailsTabs.cshtml");
        var compactCss = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/63-party-details.css");
        var tabbedParties = new[] { "Customers", "Suppliers", "Partners", "Sarrafs", "Employees", "ServiceProviders" };
        var allParties = tabbedParties.Append("Drivers").Append("Companies");

        Assert.Contains("~/css/ptg/63-party-details.css", layout);
        Assert.Contains("ptg-tabs-rail ak-detail-tabs", tabsPartial);
        Assert.Contains(".ak-party-page > .ak-detail-header", compactCss);
        Assert.Contains(".ak-party-page > .ak-page-actions", compactCss);
        Assert.Contains("--ptg-tabs-space-above: 0", compactCss);

        foreach (var controller in allParties)
        {
            var details = ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Details.cshtml");
            Assert.Contains("ak-party-page", details);
        }

        foreach (var controller in tabbedParties)
        {
            var details = ReadRepoFile($"src/PTGOilSystem.Web/Views/{controller}/Details.cshtml");
            var tabsIndex = details.IndexOf("_DetailsTabs.cshtml", StringComparison.Ordinal);
            var firstBlockIndex = details.IndexOf("ak-detail-section", StringComparison.Ordinal);
            Assert.True(tabsIndex >= 0, $"{controller} must render the shared details tabs partial.");
            Assert.True(firstBlockIndex >= 0, $"{controller} must render at least one detail block.");
            Assert.True(
                tabsIndex < firstBlockIndex,
                $"{controller} must expose profile navigation before its detail blocks.");
        }

        var driverDetails = ReadRepoFile("src/PTGOilSystem.Web/Views/Drivers/Details.cshtml");
        Assert.Contains("_DetailsTabs.cshtml", driverDetails);
        Assert.Contains("Context.Request.Query[\"tab\"]", driverDetails);
    }

    [Fact]
    public void Master_Data_Forms_Expose_New_Fields_In_Create_And_Edit_Surfaces()
    {
        AssertViewContains("Products", "Category", "Notes");
        AssertViewContains("Locations", "Code", "IsActive", "Notes");
        AssertViewContains("ExpenseTypes", "Notes");
        AssertViewContains("Partners", "Address", "Email");
        AssertViewContains("Companies", "Address", "Notes");
        AssertViewContains("Suppliers", "Code", "Address", "Notes");
        AssertViewContains("Customers", "Code", "Address", "Notes");
        AssertViewContains("Terminals", "Notes");
        AssertViewContains("StorageTanks", "DisplayName", "IsActive", "Notes");
        AssertViewContains("Trucks", "Notes");
        AssertViewContains("Wagons", "WagonNumber", "WagonType", "Owner", "CapacityMt", "IsActive", "Notes");
        AssertViewContains("Drivers", "NationalId", "Address", "Notes");
        AssertViewContains("Vessels", "Code", "OwnerOrOperator", "Notes");
        AssertViewContains("CashAccounts", "AccountNumber", "BankName", "Branch");
    }

    [Fact]
    public void Location_And_StorageTank_Delete_Use_Soft_Archive_When_Referenced()
    {
        var safety = ReadRepoFile("src/PTGOilSystem.Web/Services/DeleteSafety/MasterDataDeleteSafetyService.cs");
        var locationsController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/LocationsController.cs");
        var storageTanksController = ReadRepoFile("src/PTGOilSystem.Web/Controllers/StorageTanksController.cs");

        Assert.Contains("return BuildArchivableResult(usageAreas);", ExtractMethod(safety, "EvaluateLocationAsync"));
        Assert.Contains("return BuildArchivableResult(usageAreas);", ExtractMethod(safety, "EvaluateStorageTankAsync"));
        Assert.Contains("item.IsActive = false;", ExtractMethod(locationsController, "Delete"));
        Assert.Contains("item.IsActive = false;", ExtractMethod(storageTanksController, "Delete"));
    }

    private static void AssertStringProperty<T>(string name, int maxLength)
    {
        var property = typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.Equal(maxLength, property.GetCustomAttribute<MaxLengthAttribute>()?.Length);
    }

    private static void AssertTrueByDefault<T>(string name) where T : new()
    {
        var property = typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property!.PropertyType);
        Assert.True((bool)(property.GetValue(new T()) ?? false));
    }

    private static void AssertViewContains(string viewFolder, params string[] fields)
    {
        var createFormPath = $"src/PTGOilSystem.Web/Views/{viewFolder}/_CreateForm.cshtml";
        if (!File.Exists(GetRepoPath(createFormPath)))
        {
            createFormPath = $"src/PTGOilSystem.Web/Views/{viewFolder}/Create.cshtml";
        }

        var createForm = ReadRepoFile(createFormPath);
        var edit = ReadRepoFile($"src/PTGOilSystem.Web/Views/{viewFolder}/Edit.cshtml");

        foreach (var field in fields)
        {
            Assert.Contains($"asp-for=\"{field}\"", createForm + edit);
        }
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf($"{methodName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method {methodName} was not found.");

        var nextPublicMethod = source.IndexOf("\n    public ", start + methodName.Length, StringComparison.Ordinal);
        var nextPrivateMethod = source.IndexOf("\n    private ", start + methodName.Length, StringComparison.Ordinal);
        var candidates = new[] { nextPublicMethod, nextPrivateMethod }.Where(index => index > start).ToArray();
        var end = candidates.Length == 0 ? source.Length : candidates.Min();

        return source[start..end];
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    private static string GetRepoPath(string relativePath)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
