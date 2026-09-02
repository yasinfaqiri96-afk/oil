using System.Reflection;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-05 — نگهبانِ ساختاریِ حلقهٔ «فرم تا ذخیره».
///
/// ستونِ نسخه بدون این حلقه فقط پنجرهٔ چند میلی‌ثانیه‌ایِ داخلِ یک درخواست را می‌بندد.
/// آنچه واقعاً داده را از بین می‌برد، پنجرهٔ بلندِ «فرم باز است» است. این تست‌ها سه چیز را
/// pin می‌کنند تا آن حلقه بی‌صدا باز نشود:
///
///   ۱. هر ViewModelِ ویرایشِ هدف فیلد <c>Version</c> دارد،
///   ۲. هر فرمِ ویرایشِ هدف آن را به‌صورت hidden می‌فرستد،
///   ۳. هر اکشنِ <c>Edit</c>ِ هدف آن را با <c>UseExpectedVersion</c> به EF می‌دهد.
///
/// بدون این‌ها، حذفِ یک خط در آینده محافظ را خاموش می‌کرد و هیچ تستِ رفتاری‌ای قرمز نمی‌شد،
/// چون ذخیرهٔ موفق در حالتِ بی‌محافظ هم موفق است.
/// </summary>
public sealed class ConcurrencyVersionFormCoverageTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>موجودیت، ViewModel، فایل View و نامِ کنترلرِ هر مسیرِ ویرایشِ محافظت‌شده.</summary>
    public static TheoryData<string, string, string> GuardedEditRoutes() => new()
    {
        { "PaymentsController.cs",                "payment",  "Views/Payments/Create.cshtml" },
        { "ExpensesController.cs",                "expense",  "Views/Expenses/Create.cshtml" },
        { "SalesController.cs",                   "sale",     "Views/Sales/Create.cshtml" },
        { "ContractsController.cs",               "existing", "Views/Contracts/Edit.cshtml" },
        { "DispatchController.cs",                "dispatch", "Views/Dispatch/Create.cshtml" },
        { "LoadingController.cs",                 "loading",  "Views/Loading/Edit.cshtml" },
        { "LossEventsController.cs",              "item",     "Views/LossEvents/Edit.cshtml" },
        { "InventoryTransportLegsController.cs",  "leg",      "Views/InventoryTransportLegs/Edit.cshtml" },
    };

    [Theory]
    [MemberData(nameof(GuardedEditRoutes))]
    public void Edit_Post_Binds_The_Version_The_User_Saw(string controllerFile, string variable, string viewFile)
    {
        _ = viewFile;

        var source = ReadWebFile($"Controllers/{controllerFile}");
        Assert.Contains(
            $"_db.UseExpectedVersion({variable}, model.Version);",
            source);
    }

    [Theory]
    [MemberData(nameof(GuardedEditRoutes))]
    public void Edit_Form_Round_Trips_The_Version_As_A_Hidden_Field(string controllerFile, string variable, string viewFile)
    {
        _ = controllerFile;
        _ = variable;

        var source = ReadWebFile(viewFile);
        Assert.Contains("asp-for=\"Version\"", source);
        Assert.Contains("type=\"hidden\"", source);
    }

    [Fact]
    public void Every_Guarded_Edit_View_Model_Exposes_A_Version_Field()
    {
        var assembly = typeof(PTGOilSystem.Web.Data.ApplicationDbContext).Assembly;

        string[] viewModels =
        [
            "PaymentCreateViewModel",
            "ExpenseCreateViewModel",
            "SalesCreateViewModel",
            "ContractFormViewModel",
            "DispatchCreateViewModel",
            "LoadingEditViewModel",
            "LossEventCreateViewModel",
            "InventoryTransportLegCreateViewModel",
        ];

        foreach (var name in viewModels)
        {
            var type = assembly.GetTypes().SingleOrDefault(t => t.Name == name);
            Assert.NotNull(type);

            var property = type!.GetProperty("Version", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{name} فیلد Version ندارد.");
            Assert.Equal(typeof(long), property!.PropertyType);
            Assert.True(property.CanRead && property.CanWrite);
        }
    }

    /// <summary>
    /// موجودیت‌هایی که نشانهٔ هم‌زمانی دارند، عمداً انتخاب شده‌اند. این تست فهرست را
    /// pin می‌کند تا نه کسی بی‌خبر یکی را بردارد، نه سندِ فقط-افزودنی الکی نشانه بگیرد.
    /// </summary>
    [Fact]
    public void The_Set_Of_Versioned_Entities_Is_Exactly_The_Reviewed_List()
    {
        var expected = new[]
        {
            nameof(Contract),
            nameof(ContractPartner),
            nameof(ExpenseTransaction),
            nameof(InventoryTransportLeg),
            nameof(LoadingRegister),
            nameof(LossEvent),
            nameof(PaymentTransaction),
            nameof(SalesTransaction),
            nameof(TruckDispatch),
        };

        var actual = typeof(PTGOilSystem.Web.Data.ApplicationDbContext).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IVersionedEntity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }

    private static string ReadWebFile(string relativePath)
    {
        var full = Path.Combine(RepositoryRoot, "src", "PTGOilSystem.Web", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"فایل پیدا نشد: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "ریشهٔ مخزن پیدا نشد.");
        return directory!.FullName;
    }
}
