using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد ظاهری صفحهٔ پشتیبان‌گیری. هدف این است که ساده‌سازی صفحه با یک ویرایش
/// بعدی بی‌سروصدا برنگردد: چهار کارت، یک دکمهٔ اصلی، دو بخش بستهٔ Accordion و
/// جدول تاریخچه‌ای که همیشه دیده می‌شود.
/// </summary>
public class BackupsPageStructureTests
{
    private static readonly string IndexView = ReadRepoFile("src/PTGOilSystem.Web/Views/Backups/Index.cshtml");
    private static readonly string LiveView = ReadRepoFile("src/PTGOilSystem.Web/Views/Backups/_BackupLive.cshtml");
    private static readonly string Script = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/js/backups.js");
    private static readonly string Styles = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/66-backups.css");
    private static readonly string Layout = ReadRepoFile("src/PTGOilSystem.Web/Views/Shared/_Layout.cshtml");
    private static readonly string PageFrame = ReadRepoFile("src/PTGOilSystem.Web/wwwroot/css/ptg/70-page-frame.css");

    /// <summary>
    /// چهار کارت وضعیت باید از کامپوننت مشترک آماری سیستم بیایند، نه از کارت
    /// اختصاصی این صفحه؛ در غیر این صورت طراحی صفحه از بقیهٔ سیستم جدا می‌افتد.
    /// </summary>
    [Fact]
    public void The_Status_Row_Has_Exactly_Four_Shared_Stat_Cards()
    {
        Assert.Equal(4, CountOccurrences(LiveView, "<vc:stat-card"));
        Assert.Contains("class=\"ak-stat-grid\"", LiveView, StringComparison.Ordinal);
        Assert.DoesNotContain("bk-card", LiveView, StringComparison.Ordinal);
        Assert.DoesNotContain("bk-card", Styles, StringComparison.Ordinal);

        // ناحیهٔ زنده باید ریتم عمودی داشته باشد، وگرنه لیست به کارت‌ها می‌چسبد.
        Assert.Contains(".bk-page [data-backup-live]", Styles, StringComparison.Ordinal);
        Assert.Contains(".bk-page .ak-stat-grid", Styles, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Page_Exposes_A_Single_Primary_Backup_Button()
    {
        Assert.Equal(1, CountOccurrences(LiveView, "data-backup-create"));
        Assert.Contains("id=\"backup-create-now\"", LiveView, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Backup_Button_Opts_Out_Of_The_Global_Double_Submit_Guard()
    {
        // ریشهٔ باگ «دکمهٔ همیشه‌چرخان»: محافظ عمومی دکمه را قفل می‌کرد و هرگز آزاد نمی‌شد.
        Assert.Contains("data-no-submit-guard", LiveView, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-action=\"CreateNow\"", LiveView, StringComparison.Ordinal);
    }

    /// <summary>
    /// صفحه بدون تیتر و توضیح بالای آن شروع می‌شود و دکمهٔ ایجاد بکاپ در هدر
    /// لیست تاریخچه می‌نشیند، نه در یک نوار اکشن جداگانه.
    /// </summary>
    [Fact]
    public void The_Primary_Action_Lives_In_The_History_Header_And_The_Page_Intro_Is_Gone()
    {
        Assert.DoesNotContain("bk-head", IndexView, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", IndexView, StringComparison.Ordinal);
        Assert.DoesNotContain("بکاپ کامل شامل", IndexView, StringComparison.Ordinal);
        Assert.DoesNotContain(".bk-head", Styles, StringComparison.Ordinal);

        var panelHead = LiveView.IndexOf("bk-panel-head", StringComparison.Ordinal);
        var createButton = LiveView.IndexOf("id=\"backup-create-now\"", StringComparison.Ordinal);
        var tableWrap = LiveView.IndexOf("bk-table-wrap", StringComparison.Ordinal);

        Assert.True(panelHead > 0);
        Assert.True(createButton > panelHead, "The create button must sit inside the history list header.");
        Assert.True(createButton < tableWrap, "The create button must stay above the history table.");
        Assert.Contains("bk-panel-actions", LiveView, StringComparison.Ordinal);
        Assert.Contains(".bk-page .bk-panel-actions", Styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_Configuration_Sections_Are_Collapsed_By_Default()
    {
        Assert.Equal(2, CountOccurrences(IndexView, "accordion-button collapsed"));
        Assert.Equal(2, CountOccurrences(IndexView, "accordion-collapse collapse\""));
        Assert.Contains("id=\"bk-drive-body\"", IndexView, StringComparison.Ordinal);
        Assert.Contains("id=\"bk-auto-body\"", IndexView, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Connected_Drive_Never_Renders_The_Client_Secret_Back_To_The_Page()
    {
        // فرم اعتبارنامه فقط در حالت «متصل نیست» یا با «ویرایش اتصال» دیده می‌شود و
        // هیچ‌وقت مقدار ذخیره‌شده را در value برنمی‌گرداند.
        Assert.Contains("data-drive-edit-toggle", IndexView, StringComparison.Ordinal);
        Assert.Contains("hidden=\"@(Model.Drive.IsConnected ? \"hidden\" : null)\"", IndexView, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"@Model.Drive.ClientId", IndexView, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedClientSecret", IndexView, StringComparison.Ordinal);
    }

    [Fact]
    public void The_History_Table_Keeps_All_Nine_Required_Columns()
    {
        Assert.Contains("id=\"backup-history-table\"", LiveView, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(LiveView, "</th>"));
        Assert.Contains("colspan=\"9\"", LiveView, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_Rows_Offer_The_Real_Error_Through_A_Modal()
    {
        Assert.Contains("data-backup-error=\"@row.ErrorMessage\"", LiveView, StringComparison.Ordinal);
        Assert.Contains("id=\"backup-error-modal\"", IndexView, StringComparison.Ordinal);
        Assert.Contains("data-backup-error-text", IndexView, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Live_Region_Carries_The_State_The_Poller_Needs()
    {
        Assert.Contains("data-backup-live", LiveView, StringComparison.Ordinal);
        Assert.Contains("data-running=", LiveView, StringComparison.Ordinal);
        Assert.Contains("data-queued=", LiveView, StringComparison.Ordinal);
        Assert.Contains("data-latest-status=", LiveView, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Error_Modal_Lives_Outside_The_Polled_Region()
    {
        var liveStart = IndexView.IndexOf("<partial name=\"_BackupLive\"", StringComparison.Ordinal);
        var modalStart = IndexView.IndexOf("id=\"backup-error-modal\"", StringComparison.Ordinal);

        Assert.True(liveStart > 0);
        Assert.True(modalStart > liveStart, "The error modal must not sit inside the region that polling replaces.");
    }

    [Fact]
    public void The_Script_Clears_The_Loading_State_On_Every_Path()
    {
        Assert.Contains(".catch(", Script, StringComparison.Ordinal);
        Assert.Contains(".finally(", Script, StringComparison.Ordinal);
        Assert.Contains("setButtonBusy(createButton(), false)", Script, StringComparison.Ordinal);
        // سقف ایمنی: حتی اگر سرور هیچ‌وقت «تمام شد» نگوید، Poll بی‌نهایت ادامه نمی‌یابد.
        Assert.Contains("POLL_MAX_MS", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Polling_Stops_As_Soon_As_The_Run_Is_Neither_Running_Nor_Queued()
    {
        Assert.Contains("stopPolling", Script, StringComparison.Ordinal);
        Assert.Contains("announceResult", Script, StringComparison.Ordinal);
        Assert.Contains("ptgToast", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Page_Stylesheet_Is_Scoped_And_Uses_The_Product_Primary_Colour()
    {
        Assert.Contains("--bk-primary: #1877F2", Styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", Styles, StringComparison.Ordinal);
        Assert.Contains("66-backups.css", Layout, StringComparison.Ordinal);

        // هیچ قاعده‌ای نباید بیرون از .bk-page یا مودال اختصاصی صفحه اثر بگذارد.
        foreach (var line in Styles.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.EndsWith('{') || trimmed.StartsWith('@') || trimmed.StartsWith("/*"))
            {
                continue;
            }

            Assert.True(
                trimmed.StartsWith(".bk-page", StringComparison.Ordinal)
                || trimmed.StartsWith("#backup-error-modal", StringComparison.Ordinal),
                $"Unscoped rule in 66-backups.css: {trimmed}");
        }
    }

    /// <summary>
    /// عرض صفحه باید با بقیهٔ صفحه‌ها یکسان باشد: سقف عرض و Gutter افقی فقط در
    /// PageFrame تعیین می‌شود و این صفحه سقف اختصاصی یا Padding افقی ندارد.
    /// </summary>
    [Fact]
    public void The_Page_Takes_Its_Width_From_The_Shared_PageFrame()
    {
        var root = Styles[Styles.IndexOf(".bk-page {", StringComparison.Ordinal)..];
        root = root[..root.IndexOf('}')];

        Assert.Contains("max-width: 100%", root, StringComparison.Ordinal);
        Assert.Contains("width: 100%", root, StringComparison.Ordinal);
        Assert.Contains("padding: 0;", root, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-inline: auto", root, StringComparison.Ordinal);
        Assert.DoesNotContain("1180px", Styles, StringComparison.Ordinal);

        // هیچ Padding افقی اختصاصی، از جمله در بخش موبایل، باقی نمانده باشد.
        Assert.DoesNotContain("padding: 16px 14px 32px", Styles, StringComparison.Ordinal);

        // PageFrame باید این صفحه را مثل بقیهٔ ریشه‌های صفحه بشناسد.
        Assert.Contains(".bk-page", PageFrame, StringComparison.Ordinal);
    }

    /// <summary>
    /// ریشهٔ باگ «بکاپ خودکار فعال نمی‌شود»: فیلد پنهانِ false قبل از چک‌باکس بود و
    /// Model Binding اولین مقدارِ ارسالی را می‌خواند، پس مقدار همیشه false می‌شد.
    /// </summary>
    [Fact]
    public void The_Auto_Backup_Switch_Posts_True_When_It_Is_Checked()
    {
        var checkbox = IndexView.IndexOf("id=\"AutoBackupEnabled\"", StringComparison.Ordinal);
        var hiddenFallback = IndexView.IndexOf("type=\"hidden\" name=\"AutoBackupEnabled\" value=\"false\"", StringComparison.Ordinal);

        Assert.True(checkbox > 0, "The automatic-backup switch is missing.");
        Assert.True(hiddenFallback > 0, "The false fallback is required so switching off also posts a value.");
        Assert.True(
            hiddenFallback > checkbox,
            "The hidden false fallback must come after the checkbox, otherwise the switch can never post true.");
    }

    [Fact]
    public void The_Restore_Uploads_Switch_Posts_True_When_It_Is_Checked()
    {
        var restoreView = ReadRepoFile("src/PTGOilSystem.Web/Views/BackupRestore/Index.cshtml");

        var checkbox = restoreView.IndexOf("id=\"RestoreUploads\"", StringComparison.Ordinal);
        var hiddenFallback = restoreView.IndexOf("type=\"hidden\" name=\"RestoreUploads\" value=\"false\"", StringComparison.Ordinal);

        Assert.True(checkbox > 0);
        Assert.True(hiddenFallback > checkbox, "The hidden false fallback must come after the checkbox.");
    }

    /// <summary>
    /// «ایجاد بکاپ» آبی می‌ماند و «ذخیره تنظیمات» سبزِ توکن مشترک سیستم می‌شود.
    /// </summary>
    [Fact]
    public void The_Create_Button_Stays_Blue_And_The_Save_Button_Is_Green()
    {
        Assert.Contains("--bk-primary: #1877F2", Styles, StringComparison.Ordinal);
        Assert.Contains("--bk-success: var(--ptg-success)", Styles, StringComparison.Ordinal);
        Assert.Contains(".bk-page .btn.bk-primary.bk-success", Styles, StringComparison.Ordinal);

        // ریشهٔ باگ «دکمه‌ها سفید می‌مانند»: سیستم دکمهٔ پوسته رنگ را با
        // «background: var(--_bg) !important» می‌نشاند و پیش‌فرضش سفید است، پس رنگ
        // باید با همان قرارداد متغیر ست شود، نه با background ساده.
        Assert.Contains("--_bg: var(--bk-primary)", Styles, StringComparison.Ordinal);
        Assert.Contains("--_bg: var(--bk-success)", Styles, StringComparison.Ordinal);
        Assert.DoesNotContain("background: var(--bk-primary)", Styles, StringComparison.Ordinal);
        Assert.DoesNotContain("background: var(--bk-success)", Styles, StringComparison.Ordinal);

        // دکمهٔ ایجاد بکاپ نباید کلاس سبز بگیرد.
        Assert.DoesNotContain("bk-primary bk-success", LiveView, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-primary bk-primary bk-success\"", IndexView, StringComparison.Ordinal);

        // فقط ذخیره تنظیمات سبز است، اتصال Drive آبی می‌ماند.
        Assert.Equal(1, CountOccurrences(IndexView, "bk-success"));
    }

    [Fact]
    public void Mobile_Collapses_The_Settings_Grid_To_One_Column()
    {
        var mobileBlock = Styles[Styles.IndexOf("@media (max-width: 720px)", StringComparison.Ordinal)..];

        Assert.Contains(".bk-page .bk-grid { grid-template-columns: 1fr; }", mobileBlock, StringComparison.Ordinal);
        Assert.Contains(".bk-page .bk-facts { grid-template-columns: 1fr; }", mobileBlock, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
        => haystack.Split(needle, StringSplitOptions.None).Length - 1;

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found from the test output directory: {relativePath}");
    }
}
