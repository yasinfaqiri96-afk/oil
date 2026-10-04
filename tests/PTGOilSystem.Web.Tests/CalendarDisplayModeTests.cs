using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.ModelBinding;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Calendars;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Time;
using PTGOilSystem.Web.TagHelpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// لایهٔ تقویم در کل برنامه: حالت میلادی دقیقاً مثل قبل، حالت هجری شمسی فقط نمایش/ورود را عوض می‌کند
/// و هیچ مقدارِ ذخیره‌شده‌ای تغییر نمی‌کند. هر تست تقویم را در محدودهٔ AsyncLocal خودش عوض می‌کند تا
/// روی تست‌های موازی اثر نگذارد.
/// </summary>
public class CalendarDisplayModeTests
{
    private static readonly DateTime BusinessDate = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
    private const char Lrm = '‎';

    // ---- Gregorian mode: unchanged ------------------------------------------------

    [Fact]
    public void Gregorian_Is_The_Default()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);

        Assert.Equal(CalendarType.Gregorian, new SystemSetting().CalendarType);
        Assert.False(DateDisplay.IsSolarHijri);
    }

    [Fact]
    public void Gregorian_Mode_Output_Is_Unchanged()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        var value = new DateTime(2026, 10, 4, 14, 35, 0, DateTimeKind.Utc);

        Assert.Equal($"{Lrm}2026/10/04{Lrm}", value.ToDisplayDate());
        Assert.Equal($"{Lrm}2026/10/04 14:35{Lrm}", value.ToDisplayDateTime());
        Assert.Equal(value.ToString("yyyy-MM-dd"), value.ToCalendarString("yyyy-MM-dd"));
        Assert.Equal(value.ToString("yyyy/MM/dd HH:mm"), value.ToCalendarString("yyyy/MM/dd HH:mm"));
        Assert.Equal(string.Empty, ((DateTime?)null).ToCalendarString("yyyy-MM-dd"));
        Assert.Equal("2026-10-04", DateDisplay.IsoTextToDisplay("2026-10-04"));
        Assert.Equal("2026-10-04", TabularExportCell.Date(value).ToDisplayText(false));
        Assert.Equal("2026-10-04", CsvExportSupport.Date(value));
    }

    // ---- Afghan Solar Hijri mode ------------------------------------------------------

    [Fact]
    public void Solar_Mode_Displays_Afghan_Solar_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        var value = new DateTime(2026, 10, 4, 14, 35, 0, DateTimeKind.Utc);

        Assert.Equal($"{Lrm}1405/07/12{Lrm}", value.ToDisplayDate());
        Assert.Equal($"{Lrm}1405/07/12 14:35{Lrm}", value.ToDisplayDateTime());
        Assert.Equal("1405-07-12", value.ToCalendarString("yyyy-MM-dd"));
        Assert.Equal("1405/07/12", DateDisplay.PlainDate(value));
        Assert.Equal("12 میزان 1405", DateDisplay.LongDateText(value));
        Assert.Equal("1405/07/12", DateDisplay.IsoTextToDisplay("2026-10-04"));
        Assert.Equal("not-a-date", DateDisplay.IsoTextToDisplay("not-a-date"));
        Assert.Equal("1405-07-12", TabularExportCell.Date(value).ToDisplayText(false));
        Assert.Equal("1405-07-12", CsvExportSupport.Date(value));
    }

    [Fact]
    public void Solar_Mode_Keeps_The_Html_Date_Input_Value_Canonical()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        // مقدارِ input تاریخ همیشه ISO میلادی است؛ binding و اسکریپت‌ها روی همین کار می‌کنند.
        Assert.Equal("2026-10-04", BusinessDate.ToHtmlDateInput());
    }

    [Fact]
    public void Business_Dates_Do_Not_Shift_But_Timestamps_Are_Shown_In_Kabul_Time()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        // تاریخِ تجاری (نیمه‌شبِ کانونیک) بدون جابه‌جایی منطقهٔ زمانی همان روز می‌ماند.
        Assert.Equal($"{Lrm}1405/07/12{Lrm}", BusinessDate.ToDisplayDate());

        // timestamp سیستمی: 20:00 UTC روزِ ۳ اکتبر = 00:30 روزِ ۴ اکتبر در کابل = ۱۲ میزان.
        var auditUtc = new DateTime(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal($"{Lrm}1405/07/12 00:30{Lrm}", auditUtc.ToAfghanistanDisplayDateTime());
    }

    [Fact]
    public void Calendar_Scope_Does_Not_Leak()
    {
        using (AppCalendarContext.Use(CalendarType.Gregorian))
        {
            using (AppCalendarContext.Use(CalendarType.AfghanSolarHijri))
            {
                AppCalendarContext.Set(CalendarType.AfghanSolarHijri);
                Assert.True(DateDisplay.IsSolarHijri);
            }

            Assert.False(DateDisplay.IsSolarHijri);
        }
    }

    // ---- central service ----------------------------------------------------------------

    [Fact]
    public void Service_Parses_Solar_And_Gregorian_In_Solar_Mode()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        var service = NewService();

        Assert.True(service.IsSolarHijri);
        Assert.True(service.TryParseDate("۱۴۰۵/۰۷/۱۲", out var fromSolar, out _));
        Assert.True(service.TryParseDate("2026-10-04", out var fromIso, out _));
        Assert.Equal(BusinessDate, fromSolar);
        Assert.Equal(BusinessDate, fromIso);
        Assert.False(service.TryParseDate("1404/12/30", out _, out var error));
        Assert.Equal("ماه حوت سال 1404 فقط 29 روز دارد.", error);
        Assert.Equal("1405/07/12", service.FormatDate(BusinessDate));
        Assert.Equal("12 میزان 1405", service.FormatLongDate(BusinessDate));
        Assert.Equal("-", service.FormatDate(null));
        Assert.Equal("1405/07/12", service.TodayText);
    }

    [Fact]
    public void Service_Does_Not_Read_Solar_Text_In_Gregorian_Mode()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        var service = NewService();

        Assert.True(service.TryParseDate("2026-10-04", out var value, out _));
        Assert.Equal(BusinessDate, value);
        Assert.False(service.TryParseDate("۱۴۰۵/۰۷/۱۲", out _, out _));
        Assert.Equal("2026/10/04", service.FormatDate(BusinessDate));
    }

    [Fact]
    public void Report_Range_Converts_Solar_Bounds_To_Canonical_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        var service = NewService();

        var range = service.ParseRange("1405/01/01", "1405/07/12");
        Assert.True(range.IsValid);
        Assert.Equal(new DateTime(2026, 3, 21), range.From);
        Assert.Equal(BusinessDate, range.To);

        var open = service.ParseRange(null, "");
        Assert.True(open.IsValid);
        Assert.Null(open.From);
        Assert.Null(open.To);

        var reversed = service.ParseRange("1405/07/12", "1405/01/01");
        Assert.False(reversed.IsValid);

        var invalid = service.ParseRange("1405/13/01", null);
        Assert.Equal("از تاریخ: " + CalendarMessages.Month, Assert.Single(invalid.Errors));
    }

    [Fact]
    public void Solar_Month_And_Year_Ranges_Are_Canonical_Dates()
    {
        var service = NewService();

        Assert.Equal((new DateTime(2026, 9, 23), new DateTime(2026, 10, 22)), service.SolarMonthRange(1405, 7));
        Assert.Equal((new DateTime(2027, 2, 20), new DateTime(2027, 3, 20)), service.SolarMonthRange(1405, 12));
        Assert.Equal((new DateTime(2024, 3, 20), new DateTime(2025, 3, 20)), service.SolarYearRange(1403));   // کبیسه
        Assert.Equal((new DateTime(2026, 3, 21), new DateTime(2027, 3, 20)), service.SolarYearRange(1405));
    }

    // ---- model binding (forms, filters, edit) ------------------------------------

    [Theory]
    [InlineData("1405/07/12")]
    [InlineData("۱۴۰۵/۰۷/۱۲")]
    [InlineData("١٤٠٥/٠٧/١٢")]
    [InlineData("2026-10-04")]
    public async Task Binder_Converts_Solar_Input_To_The_Canonical_Date(string raw)
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        var context = await BindAsync(typeof(DateTime), "PaymentDate", raw);

        Assert.True(context.Result.IsModelSet);
        var value = Assert.IsType<DateTime>(context.Result.Model);
        Assert.Equal(BusinessDate, value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(0, context.ModelState.ErrorCount);
    }

    [Theory]
    [InlineData("1404/12/30", "ماه حوت سال 1404 فقط 29 روز دارد.")]
    [InlineData("1405/13/01", CalendarMessages.Month)]
    [InlineData("۱۴۰۵/۰۷/۳۱", "ماه میزان سال 1405 فقط 30 روز دارد.")]
    public async Task Binder_Rejects_Invalid_Solar_Input_With_A_Dari_Message(string raw, string message)
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        var context = await BindAsync(typeof(DateTime), "PaymentDate", raw);

        Assert.False(context.Result.IsModelSet);
        Assert.Equal(1, context.ModelState.ErrorCount);
        Assert.Equal(message, Assert.Single(context.ModelState["PaymentDate"]!.Errors).ErrorMessage);
    }

    [Fact]
    public async Task Binder_Leaves_Optional_Dates_Empty()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        var context = await BindAsync(typeof(DateTime?), "Filter.FromDate", "");

        Assert.Null(context.Result.Model);
        Assert.Equal(0, context.ModelState.ErrorCount);
    }

    [Fact]
    public async Task Binder_Is_Unchanged_In_Gregorian_Mode()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);

        var iso = await BindAsync(typeof(DateTime), "PaymentDate", "2026-10-04");
        Assert.Equal(BusinessDate, iso.Result.Model);

        // در حالت میلادی متنِ «1405/07/12» شمسی تفسیر نمی‌شود (همان رفتارِ قبلیِ binder).
        var solarText = await BindAsync(typeof(DateTime), "PaymentDate", "1405/07/12");
        Assert.Equal(1405, Assert.IsType<DateTime>(solarText.Result.Model).Year);
    }

    [Fact]
    public async Task Report_Filter_Runs_The_Same_Query_On_Canonical_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        var from = (DateTime?)(await BindAsync(typeof(DateTime?), "Filter.FromDate", "1405/07/01")).Result.Model;
        var to = (DateTime?)(await BindAsync(typeof(DateTime?), "Filter.ToDate", "1405/07/30")).Result.Model;

        var rows = new[]
        {
            new DateTime(2026, 9, 22), // 1405/06/31 — بیرون
            new DateTime(2026, 10, 22), // 1405/07/30 — داخل
            new DateTime(2026, 9, 23), // 1405/07/01 — داخل
            new DateTime(2026, 10, 23) // 1405/08/01 — بیرون
        };

        // همان شرطِ موجودِ گزارش‌ها روی تاریخِ واقعی؛ مرتب‌سازی هم روی مقدارِ تاریخ است نه متن.
        var result = rows.Where(d => d >= from && d <= to).OrderBy(d => d).ToList();

        Assert.Equal([new DateTime(2026, 9, 23), new DateTime(2026, 10, 22)], result);
    }

    [Fact]
    public async Task Fiscal_Year_Bounds_Entered_In_Solar_Are_Stored_As_Canonical_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        var start = (DateTime)(await BindAsync(typeof(DateTime), "startDate", "1405/01/01")).Result.Model!;
        var end = (DateTime)(await BindAsync(typeof(DateTime), "endDate", "1405/12/29")).Result.Model!;

        Assert.Equal((start, end), NewService().SolarYearRange(1405));

        await using var db = NewDb();
        db.FiscalYears.Add(new FiscalYear { CompanyId = 1, Name = "FY-1405", StartDate = start, EndDate = end });
        await db.SaveChangesAsync();

        var stored = await db.FiscalYears.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 3, 21), stored.StartDate);
        Assert.Equal(new DateTime(2027, 3, 20), stored.EndDate);
        Assert.Equal($"{Lrm}1405/01/01{Lrm}", stored.StartDate.ToDisplayDate());
        Assert.Equal($"{Lrm}1405/12/29{Lrm}", stored.EndDate.ToDisplayDate());
    }

    [Fact]
    public async Task Creating_And_Editing_A_Record_In_Solar_Stores_Canonical_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);
        await using var db = NewDb();

        // ثبت: کاربر «۱۲ میزان ۱۴۰۵» را وارد می‌کند.
        var created = (DateTime)(await BindAsync(typeof(DateTime), "Date", "۱۴۰۵/۰۷/۱۲")).Result.Model!;
        db.HrHolidays.Add(new HrHoliday { Date = created, Name = "test" });
        await db.SaveChangesAsync();
        var holiday = await db.HrHolidays.SingleAsync();
        Assert.Equal(BusinessDate, holiday.Date);

        // فرمِ ویرایش: مقدار ISO می‌ماند و معادلِ شمسی از سرور می‌آید.
        var output = RenderDateInput(holiday.Date.ToHtmlDateInput());
        Assert.Equal("2026-10-04", output.Attributes["value"].Value);
        Assert.Equal("1405/07/12", output.Attributes["data-solar-value"].Value);

        // ویرایش: یک روز بعد.
        holiday.Date = (DateTime)(await BindAsync(typeof(DateTime), "Date", "1405/07/13")).Result.Model!;
        await db.SaveChangesAsync();
        Assert.Equal(new DateTime(2026, 10, 5), (await db.HrHolidays.AsNoTracking().SingleAsync()).Date);
    }

    // ---- tag helper ----------------------------------------------------------------------

    [Fact]
    public void Date_Input_Tag_Helper_Is_Silent_In_Gregorian_Mode()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);

        var output = RenderDateInput("2026-10-04");

        Assert.False(output.Attributes.ContainsName("data-solar-value"));
        Assert.False(output.Attributes.ContainsName("data-calendar-input"));
        Assert.Equal(2, output.Attributes.Count);
    }

    [Fact]
    public void Date_Input_Tag_Helper_Supports_Empty_Optional_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.AfghanSolarHijri);

        var output = RenderDateInput("");

        Assert.Equal("solar", output.Attributes["data-calendar-input"].Value);
        Assert.False(output.Attributes.ContainsName("data-solar-value"));
        Assert.Equal("", output.Attributes["value"].Value);
    }

    // ---- imports ---------------------------------------------------------------------------

    [Fact]
    public void Imported_Solar_Text_Is_Never_Read_As_A_Gregorian_Year()
    {
        using (AppCalendarContext.Use(CalendarType.AfghanSolarHijri))
        {
            Assert.True(AfghanSolarCalendar.TryParseActiveCalendarText("1405/07/12", out var valid));
            Assert.Equal(BusinessDate, valid);
            Assert.True(AfghanSolarCalendar.TryParseActiveCalendarText("1404/12/30", out var invalid));
            Assert.Null(invalid);
            Assert.False(AfghanSolarCalendar.TryParseActiveCalendarText("2026-10-04", out _));
        }

        using (AppCalendarContext.Use(CalendarType.Gregorian))
        {
            Assert.False(AfghanSolarCalendar.TryParseActiveCalendarText("1405/07/12", out _));
        }
    }

    // ---- settings ----------------------------------------------------------------------------

    [Fact]
    public async Task Settings_Default_To_Gregorian_And_Persist_The_Selection()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        await using var db = NewDb();
        var settings = new CalendarSettingsService(db);

        Assert.Equal(CalendarType.Gregorian, await settings.GetAsync());

        var (_, previous) = await settings.SaveAsync(CalendarType.AfghanSolarHijri);

        Assert.Equal(CalendarType.Gregorian, previous);
        Assert.Equal(CalendarType.AfghanSolarHijri, await settings.GetAsync());
        Assert.Equal(CalendarType.AfghanSolarHijri, AppCalendarContext.Current);
        Assert.Single(await db.SystemSettings.ToListAsync());

        await settings.SaveAsync(CalendarType.Gregorian);
        Assert.Single(await db.SystemSettings.ToListAsync());
        Assert.Equal(CalendarType.Gregorian, await settings.GetAsync());
    }

    [Fact]
    public async Task Changing_The_Calendar_Never_Rewrites_Stored_Dates()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        await using var db = NewDb();
        db.HrHolidays.Add(new HrHoliday { Date = BusinessDate, Name = "nowruz-check" });
        db.FiscalYears.Add(new FiscalYear
        {
            CompanyId = 1, Name = "FY-2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31)
        });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var settings = new CalendarSettingsService(db);
        await settings.SaveAsync(CalendarType.AfghanSolarHijri);
        Assert.Equal(before, await SnapshotAsync(db));

        await settings.SaveAsync(CalendarType.Gregorian);
        Assert.Equal(before, await SnapshotAsync(db));

        static async Task<string> SnapshotAsync(ApplicationDbContext db)
        {
            var holidays = await db.HrHolidays.AsNoTracking().OrderBy(h => h.Id)
                .Select(h => $"{h.Id}:{h.Date:O}:{h.UpdatedAtUtc:O}").ToListAsync();
            var years = await db.FiscalYears.AsNoTracking().OrderBy(y => y.Id)
                .Select(y => $"{y.Id}:{y.StartDate:O}:{y.EndDate:O}:{y.UpdatedAtUtc:O}").ToListAsync();
            return string.Join("|", holidays.Concat(years));
        }
    }

    [Fact]
    public void System_Settings_Page_Is_Admin_Only_And_In_Management_Navigation()
    {
        var attribute = typeof(SystemSettingsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(AuthPolicies.AdminOnly, attribute?.Policy);
        Assert.Equal(RoleNavigationKeys.Management, RoleAccessRules.NavigationKeyForController("SystemSettings"));
        Assert.Equal(
            [(CalendarType.Gregorian, "میلادی"), (CalendarType.AfghanSolarHijri, "هجری شمسی افغانستان")],
            SystemSettingsPageViewModel.CalendarOptions);
        Assert.Equal("نوع تقویم", typeof(SystemSettingsFormViewModel)
            .GetProperty(nameof(SystemSettingsFormViewModel.CalendarType))!
            .GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()!.Name);
    }

    [Fact]
    public async Task Saving_The_Setting_From_The_Page_Applies_And_Audits_It()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        await using var db = NewDb();
        var audit = new RecordingAuditService();
        var controller = new SystemSettingsController(new CalendarSettingsService(db), new FixedClock(), audit)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        var result = await controller.Save(
            new SystemSettingsFormViewModel { CalendarType = CalendarType.AfghanSolarHijri }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(DateDisplay.IsSolarHijri);
        Assert.Contains("CalendarType: Gregorian -> AfghanSolarHijri", Assert.Single(audit.Diffs));

        var page = Assert.IsType<ViewResult>(await controller.Index(CancellationToken.None));
        var model = Assert.IsType<SystemSettingsPageViewModel>(page.Model);
        Assert.Equal(CalendarType.AfghanSolarHijri, model.Settings.CalendarType);
        Assert.Equal("2026/10/04", model.TodayGregorian);
        Assert.Equal("1405/07/12 — 12 میزان 1405", model.TodaySolar);
    }

    [Fact]
    public async Task Invalid_Calendar_Value_Is_Rejected()
    {
        using var calendar = AppCalendarContext.Use(CalendarType.Gregorian);
        await using var db = NewDb();
        var controller = new SystemSettingsController(new CalendarSettingsService(db), new FixedClock(), new RecordingAuditService())
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

        await controller.Save(new SystemSettingsFormViewModel { CalendarType = (CalendarType)99 }, CancellationToken.None);

        Assert.Empty(await db.SystemSettings.ToListAsync());
        Assert.False(DateDisplay.IsSolarHijri);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static AppCalendarService NewService() => new(new FixedClock());

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ModelBindingContext> BindAsync(Type type, string name, string raw)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(type);
        var valueProvider = new QueryStringValueProvider(
            BindingSource.Query,
            new QueryCollection(new Dictionary<string, StringValues> { [name] = raw }),
            CultureInfo.InvariantCulture);
        var context = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext { HttpContext = new DefaultHttpContext() },
            valueProvider,
            metadata,
            bindingInfo: null,
            modelName: name);

        var binder = new UtcDateTimeModelBinder(new SimpleTypeModelBinder(type, NullLoggerFactory.Instance));
        await binder.BindModelAsync(context);
        return context;
    }

    private static TagHelperOutput RenderDateInput(string value)
    {
        var output = new TagHelperOutput(
            "input",
            new TagHelperAttributeList { { "type", "date" }, { "value", value } },
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        new CalendarDateInputTagHelper().Process(
            new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "test"),
            output);
        return output;
    }

    private sealed class FixedClock : IAfghanistanBusinessClock
    {
        public DateTime Today => BusinessDate;
        public DateTimeOffset Now => new(2026, 10, 4, 10, 0, 0, TimeSpan.FromHours(4.5));
        public (DateTime StartUtc, DateTime EndUtcExclusive) UtcRange(DateTime localDate)
            => (localDate.AddHours(-4.5), localDate.AddDays(1).AddHours(-4.5));
    }

    private sealed class RecordingAuditService : IAuditService
    {
        public List<string> Diffs { get; } = [];

        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
        {
            Diffs.Add(diff ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
