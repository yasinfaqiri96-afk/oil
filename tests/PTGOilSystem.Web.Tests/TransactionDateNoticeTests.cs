using System.IO;
using System.Runtime.CompilerServices;
using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P3-A — سندِ عقب‌تاریخ/آینده باید دیده شود.
///
/// این لایه چیزی را مسدود نمی‌کند: ثبتِ عقب‌تاریخ در کارِ روزمره قانونی است (سند دیر
/// می‌رسد) و اجرا با نگهبانِ موجودی و قفلِ دوره است. چیزی که نبود، «نشانه» بود — یک
/// سالِ اشتباه‌تایپ‌شده دقیقاً مثل تاریخ درست به نظر می‌رسید.
/// </summary>
public sealed class TransactionDateNoticeTests
{
    private static readonly DateTime Today = new(2026, 8, 29);

    [Fact]
    public void TodaysDocument_IsNotMarked()
    {
        Assert.Equal(TransactionDateKind.Today, TransactionDateNotice.Classify(Today, Today));
        Assert.Null(TransactionDateNotice.Badge(TransactionDateKind.Today));
        Assert.Null(TransactionDateNotice.Warning(Today, Today));
    }

    /// <summary>ساعتِ روز نباید طبقه‌بندی را عوض کند؛ مبنا فقط خودِ روز است.</summary>
    [Fact]
    public void TheTimeOfDayIsIgnored()
        => Assert.Equal(
            TransactionDateKind.Today,
            TransactionDateNotice.Classify(Today.AddHours(23).AddMinutes(59), Today));

    [Fact]
    public void AnEarlierDate_IsBackdated()
    {
        Assert.Equal(TransactionDateKind.Backdated, TransactionDateNotice.Classify(Today.AddDays(-1), Today));
        Assert.Equal("تاریخ گذشته", TransactionDateNotice.Badge(TransactionDateKind.Backdated));
    }

    [Fact]
    public void ALaterDate_IsFutureDated()
    {
        Assert.Equal(TransactionDateKind.FutureDated, TransactionDateNotice.Classify(Today.AddDays(1), Today));
        Assert.Equal("تاریخ آینده", TransactionDateNotice.Badge(TransactionDateKind.FutureDated));
    }

    /// <summary>تعداد روز در پیام می‌آید تا «۲۰۲۵ به‌جای ۲۰۲۶» فوراً غیرعادی به نظر برسد.</summary>
    [Fact]
    public void TheWarningNamesHowManyDaysOff()
    {
        var mistypedYear = TransactionDateNotice.Warning(new DateTime(2025, 8, 29), Today);
        Assert.NotNull(mistypedYear);
        Assert.Contains("365", mistypedYear);
        Assert.Contains("پیش از امروز", mistypedYear);

        var future = TransactionDateNotice.Warning(Today.AddDays(10), Today);
        Assert.NotNull(future);
        Assert.Contains("10", future);
        Assert.Contains("بعد از امروز", future);
    }

    // ------------------------------------------------------------------
    // نگهبان ساختاری: فرم‌های اصلی باید واقعاً به این محافظ وصل باشند
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Sales/Create.cshtml")]
    [InlineData("Expenses/Create.cshtml")]
    [InlineData("Payments/Create.cshtml")]
    [InlineData("LossEvents/Create.cshtml")]
    [InlineData("Dispatch/Create.cshtml")]
    public void TheMainFinancialForms_AskForTheDateNotice(string relativeView)
    {
        var markup = ReadRepoFile($"src/PTGOilSystem.Web/Views/{relativeView}");

        Assert.Contains("data-ptg-date-guard", markup);
        // «امروز» از ساعتِ کاری کابل رندر می‌شود، نه از ساعتِ مرورگر.
        Assert.Contains("data-ptg-today=\"@BusinessClock.Today.ToString(\"yyyy-MM-dd\")\"", markup);
    }

    [Fact]
    public void TheClientGuardIsWiredIntoTheSharedShell()
    {
        var core = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/core.js");

        Assert.Contains("initializeDateGuards();", core);
        Assert.Contains("input[type=date][data-ptg-date-guard]", core);
        // فقط اطلاع‌رسانی است: هیچ preventDefault یا disable ای روی ثبت نمی‌گذارد.
        Assert.Contains("data-ptg-today", core);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, normalized);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
