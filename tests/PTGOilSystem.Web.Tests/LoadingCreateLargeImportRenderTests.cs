using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک فایل اکسل بزرگ (مثلاً ۲۳۴ سطر) پس از خطای اعتبارسنجی همان فرم را برمی‌گرداند.
/// این تست‌ها از برگشتِ دو مشکلی جلوگیری می‌کنند که آن صفحه را ده‌ها ثانیه قفل می‌کرد و
/// دکمهٔ «ذخیره» را روی حالت چرخان نگه می‌داشت.
/// </summary>
public sealed class LoadingCreateLargeImportRenderTests
{
    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var segments = new List<string> { AppContext.BaseDirectory, "..", "..", "..", "..", ".." };
        segments.AddRange(relativeSegments);
        return File.ReadAllText(Path.GetFullPath(Path.Combine([.. segments])));
    }

    [Fact]
    public void Loading_Create_View_Does_Not_Render_Rows_Twice_In_Compact_Preview()
    {
        var view = ReadRepoFile("src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml");

        // در حالت فشرده، سطرها را اسکریپت از loadingImportedRowsData می‌سازد؛ رندر همان
        // سطرها در سرور صدها کیلوبایت HTML دورریختنی می‌ساخت.
        Assert.Contains("Enumerable.Empty<LoadingCreateRowViewModel>()", view);
        Assert.DoesNotContain("Model.Rows.Take(100)", view);
    }

    [Fact]
    public void Loading_Create_View_Releases_The_Save_Button_When_No_Response_Arrives()
    {
        var view = ReadRepoFile("src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml");

        Assert.Contains("loadingSubmitWatchdogId", view);
        Assert.Contains("showOperationFailure", view);
        Assert.Contains("clearLoadingSubmitWatchdog", view);
    }

    [Fact]
    public void Loading_Create_View_Fills_The_File_Rub_Cells_Of_Virtual_Rows()
    {
        var view = ReadRepoFile("src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml");

        // سطرهای مجازی از قالبِ خالی ساخته می‌شوند؛ بدون این بازنویسی، ستون‌های
        // «روبل فی تن» و «ارزش روبلی» با وجود دادهٔ درست «-» می‌مانند.
        Assert.Contains("function refreshRowFileRubText", view);
        Assert.Contains("data-row-frub-rate-text", view);
        Assert.Contains("data-row-frub-amount-text", view);
    }

    [Fact]
    public void Loading_Create_View_Explains_Why_A_Blocked_Submit_Did_Not_Save()
    {
        var view = ReadRepoFile("src", "PTGOilSystem.Web", "Views", "Loading", "Create.cshtml");

        Assert.Contains("loadingShowSubmitBlocked", view);
        // اگر شنوندهٔ دیگری همین رویداد را لغو کند، دکمه نباید قفل بماند.
        Assert.Contains("if (event.defaultPrevented) {", view);
    }

    [Fact]
    public void System_List_Digits_Normalizes_Only_The_Added_Subtree_In_One_Batch()
    {
        var script = ReadRepoFile("src", "PTGOilSystem.Web", "wwwroot", "js", "system-list-digits.js");

        // پیمایشِ دوبارهٔ کل جدول به ازای هر گرهِ اضافه‌شده، رندر جدول‌های بزرگ را درجه‌دو می‌کرد.
        Assert.Contains("function normalizeAddedNode", script);
        Assert.Contains("requestAnimationFrame(flush)", script);
        Assert.DoesNotContain("normalizeSystemListDigits(node)", script);
    }
}
