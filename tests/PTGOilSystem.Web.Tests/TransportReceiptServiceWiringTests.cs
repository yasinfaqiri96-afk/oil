using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

// InventoryTransportReceiptService تنها نقطه‌ای است که رسید حمل را می‌نویسد: تخلیه در مخزن،
// فروش مستقیم، انتقال به موتر و تسویه همه از آن می‌گذرند. آداپترهای حسابداری پارامتر اختیاری‌اند،
// پس ساخت دستی سرویس آن‌ها را بی‌صدا null می‌گذارد و آن مسیر بدون سند حسابداری می‌ماند.
// این تست همان قرارداد سیم‌کشی را قفل می‌کند.
public class TransportReceiptServiceWiringTests
{
    [Fact]
    public void Container_Resolves_The_Receipt_Service_With_Every_Accounting_Adapter_Attached()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<ICurrencyConversionService, CurrencyConversionService>();
        services.AddScoped<IInventoryLineageWriter>(sp =>
            InventoryLineageWriterFactory.Disabled(sp.GetRequiredService<ApplicationDbContext>()));

        // هر آداپتری که سازنده می‌خواهد با یک پیاده‌سازی تهی ثبت می‌شود؛ متدهایش صدا نمی‌شود،
        // فقط resolve شدن مهم است. اضافه‌شدن آداپتر تازه به سازنده خودکار پوشش می‌گیرد.
        var adapterTypes = AccountingAdapterParameterTypes().ToList();
        Assert.NotEmpty(adapterTypes);
        foreach (var adapterType in adapterTypes)
        {
            services.AddScoped(adapterType, _ => CreateNullImplementation(adapterType));
        }

        services.AddScoped<InventoryTransportReceiptService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<InventoryTransportReceiptService>();

        foreach (var field in AdapterFields())
        {
            Assert.NotNull(field.GetValue(service));
        }
    }

    // هر آداپتری که سازنده می‌پذیرد باید در Program.cs هم ثبت شده باشد، وگرنه کانتینر
    // بی‌صدا مقدار پیش‌فرض null را می‌گذارد و آن مسیر حسابداری غیرفعال می‌شود.
    [Fact]
    public void Every_Accounting_Adapter_The_Constructor_Accepts_Is_Registered_In_Program()
    {
        var program = ReadRepoFile("src/PTGOilSystem.Web/Program.cs");

        Assert.Contains("AddScoped<InventoryTransportReceiptService>()", program);
        foreach (var adapterType in AccountingAdapterParameterTypes())
        {
            Assert.Contains($"AddScoped<{adapterType.Name},", program);
        }
    }

    // هیچ اکشنی نباید سرویس رسید را با new بسازد. تنها ساختِ مجاز، fallback سازنده برای
    // تست‌هایی است که کنترلر را بدون کانتینر می‌سازند؛ آن هم فقط روی خط «receiptService ??».
    [Theory]
    [InlineData("src/PTGOilSystem.Web/Controllers/InventoryTransportLegsController.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/InventoryTransportReceiptsController.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/SalesController.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/SalesController.Group.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/SalesController.PreSale.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/TruckSettlementsController.cs")]
    [InlineData("src/PTGOilSystem.Web/Controllers/TruckSettlementsController.GroupUnload.cs")]
    public void Transport_Write_Paths_Do_Not_Construct_The_Receipt_Service_By_Hand(string relativePath)
    {
        // فاصله و شکست خط یکدست می‌شود تا fallbackِ چندخطی هم درست تشخیص داده شود.
        var source = System.Text.RegularExpressions.Regex.Replace(
            ReadRepoFile(relativePath), @"\s+", " ");
        var totalConstructions = System.Text.RegularExpressions.Regex.Matches(
            source, @"new InventoryTransportReceiptService\(").Count;
        var allowedFallbacks = System.Text.RegularExpressions.Regex.Matches(
            source, @"receiptService \?\? new InventoryTransportReceiptService\(").Count;

        Assert.Equal(allowedFallbacks, totalConstructions);
        Assert.True(allowedFallbacks <= 1, $"{relativePath} باید فقط یک fallback سازنده داشته باشد.");
    }

    private static ConstructorInfo ServiceConstructor()
        => typeof(InventoryTransportReceiptService)
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

    private static IEnumerable<Type> AccountingAdapterParameterTypes()
        => ServiceConstructor()
            .GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.IsInterface
                && t.Namespace == "PTGOilSystem.Web.Services.Accounting");

    private static IEnumerable<FieldInfo> AdapterFields()
    {
        var adapterTypes = AccountingAdapterParameterTypes().ToHashSet();
        return typeof(InventoryTransportReceiptService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => adapterTypes.Contains(f.FieldType));
    }

    private static object CreateNullImplementation(Type interfaceType)
        => typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(DispatchProxy.Create)
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 0)
            .MakeGenericMethod(interfaceType, typeof(NeverCalledProxy))
            .Invoke(null, null)!;

    // DispatchProxy نوع پایه را ارث می‌برد، پس sealed نباشد.
    public class NeverCalledProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException(
                $"{targetMethod?.Name} must not be called by the wiring test.");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
    }
}
