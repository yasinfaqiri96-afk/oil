using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

public partial class AuditLogsController
{
    /// <summary>حداکثر ردیف خروجی؛ فهرست ممیزی می‌تواند خیلی بزرگ باشد و نباید کل جدول در حافظه بیاید.</summary>
    private const int ExportRowCap = 20_000;

    /// <summary>
    /// خروجی فهرست ممیزی و لغوها. دقیقاً همان <c>BuildFilteredQuery</c> صفحه را با همان
    /// پارامترها اجرا می‌کند؛ هیچ فیلتر جداگانه‌ای برای خروجی وجود ندارد.
    /// <paramref name="cancellationsOnly"/> همان فهرست را به رویدادهای لغو/برگشت محدود می‌کند.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(
        string? format,
        string? q = null,
        string? user = null,
        string? category = null,
        string? module = null,
        [FromQuery] string? action = null,
        string? severity = null,
        string? success = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        bool cancellationsOnly = false,
        CancellationToken ct = default)
    {
        var query = BuildFilteredQuery(q, user, category, module, action, severity, success, fromUtc, toUtc);

        if (cancellationsOnly)
        {
            query = query.Where(log =>
                log.Action.Contains("Cancel")
                || log.Action.Contains("Revers")
                || log.Action.Contains("Delete"));
        }

        var rows = await query
            .OrderByDescending(log => log.ActionAtUtc)
            .ThenByDescending(log => log.Id)
            .Take(ExportRowCap)
            .Select(log => new
            {
                log.ActionAtUtc,
                log.ActorUsername,
                log.Category,
                log.Module,
                log.Action,
                log.EntityName,
                log.EntityId,
                log.Description,
                log.IsSuccess
            })
            .ToListAsync(ct);

        var isEn = UiText.IsEn(HttpContext);
        var businessDate = AfghanistanBusinessClock.SystemToday.ToString("yyyy-MM-dd");

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = cancellationsOnly ? "PTG_Audit_Cancellations" : "PTG_Audit_Log",
            TitleFa = cancellationsOnly ? "لغوها و برگشت‌ها" : "دفتر ممیزی",
            TitleEn = cancellationsOnly ? "Cancellations & Reversals" : "Audit Log",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", businessDate),
                ("جستجو / Search", q),
                ("کاربر / User", user),
                ("دسته / Category", category),
                ("ماژول / Module", module),
                ("عملیات / Action", action),
                ("شدت / Severity", severity),
                ("از تاریخ / From", fromUtc?.ToString("yyyy-MM-dd")),
                ("تا تاریخ / To", toUtc?.ToString("yyyy-MM-dd")),
                ("سقف ردیف / Row cap", ExportRowCap.ToString())),
            Columns =
            [
                new("زمان (UTC)", "Time (UTC)", TabularExportValueType.DateTime, 18),
                new("کاربر", "User", Width: 18),
                new("دسته", "Category", Width: 16),
                new("ماژول", "Module", Width: 16),
                new("عملیات", "Action", Width: 18),
                new("موجودیت", "Entity", Width: 20),
                new("شناسه", "Entity ID", TabularExportValueType.Integer, 11),
                new("نتیجه", "Result", Width: 12),
                new("شرح", "Description", Width: 34, Wrap: true)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                // زمان ممیزی یک timestamp فنی است و عمداً UTC می‌ماند.
                TabularExportCell.DateTime(r.ActionAtUtc),
                TabularExportCell.Text(r.ActorUsername),
                TabularExportCell.Text(r.Category),
                TabularExportCell.Text(r.Module),
                TabularExportCell.Text(r.Action),
                TabularExportCell.Text(r.EntityName),
                TabularExportCell.Integer(r.EntityId),
                TabularExportCell.Text(r.IsSuccess ? (isEn ? "Success" : "موفق") : (isEn ? "Failed" : "ناموفق")),
                TabularExportCell.Text(r.Description)
            ]))
        });
    }
}
