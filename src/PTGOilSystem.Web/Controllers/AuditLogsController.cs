using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Audit;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize(Policy = AuthPolicies.AdminOnly)]
public partial class AuditLogsController : Controller
{
    private const int IndexPageSize = 20;
    private const int DefaultLimit = 250;
    private const int MaxLimit = 1000;

    private static readonly string[] SensitiveEntityNames =
    [
        "Contract", "ContractAmendment", "LoadingRegister", "LoadingReceipt",
        "SalesTransaction", "Sale", "PaymentTransaction", "LedgerEntry",
        "InventoryMovement", "DailyFxRate", "DailyPlattsPrice", "PlattsMonthlyManual",
        "Role", "User", "CashAccount", "ExpenseTransaction", "LossEvent",
        "ThreeWaySettlement", "SarrafSettlement"
    ];

    private static readonly string[] SensitiveModules =
    [
        "Contracts", "Contract", "Loading", "LoadingReceipts", "Sales", "Payments",
        "Ledger", "Inventory", "DailyFxRates", "PlattsRates", "Users", "Roles",
        "CashAccounts", "Expenses", "LossEvents", "SarrafSettlements"
    ];

    private static readonly string[] SensitiveActions =
    [
        "Delete", "Remove", "Reverse", "Cancel", "Archive", "Approve"
    ];

    private static readonly ActivityLogSeverityOption[] SeverityOptions =
    [
        new() { Value = "normal", Label = "عادی" },
        new() { Value = "sensitive", Label = "حساس" },
        new() { Value = "security", Label = "امنیتی" },
        new() { Value = "error", Label = "خطا" }
    ];

    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth"] = "ورود و امنیت",
        ["Authentication"] = "ورود و امنیت",
        ["Users"] = "کاربران",
        ["User"] = "کاربر",
        ["Roles"] = "نقش‌ها",
        ["Role"] = "نقش",
        ["AuditLogs"] = "لاگ‌ها",
        ["Contracts"] = "قراردادها",
        ["Contract"] = "قرارداد",
        ["ContractAmendment"] = "اصلاح قرارداد",
        ["Loading"] = "بارگیری",
        ["LoadingRegister"] = "بارگیری",
        ["LoadingReceipts"] = "رسید بارگیری",
        ["LoadingReceipt"] = "رسید بارگیری",
        ["Dispatch"] = "دیسپچ",
        ["Sales"] = "فروش",
        ["Sale"] = "فروش",
        ["SalesTransaction"] = "فروش",
        ["Payments"] = "پرداخت‌ها",
        ["Payment"] = "پرداخت",
        ["PaymentTransaction"] = "پرداخت",
        ["Ledger"] = "دفتر کل",
        ["LedgerEntry"] = "سند دفتر کل",
        ["Inventory"] = "موجودی",
        ["InventoryMovement"] = "حرکت موجودی",
        ["DailyFxRates"] = "نرخ ارز",
        ["DailyFxRate"] = "نرخ ارز",
        ["PlattsRates"] = "قیمت پلاتس",
        ["DailyPlattsPrice"] = "قیمت پلاتس",
        ["CashAccounts"] = "حساب نقد/بانک",
        ["CashAccount"] = "حساب نقد/بانک",
        ["Expenses"] = "هزینه‌ها",
        ["Expense"] = "هزینه",
        ["ExpenseTransaction"] = "هزینه",
        ["LossEvents"] = "کسری‌ها",
        ["LossEvent"] = "کسری",
        ["SarrafSettlements"] = "تسویه صراف",
        ["SarrafSettlement"] = "تسویه صراف",
        ["Products"] = "کالاها",
        ["Product"] = "کالا",
        ["StorageTanks"] = "مخازن",
        ["StorageTank"] = "مخزن",
        ["Trucks"] = "موترها",
        ["Truck"] = "موتر",
        ["Drivers"] = "راننده‌ها",
        ["Driver"] = "راننده",
        ["Vessels"] = "کشتی‌ها",
        ["Vessel"] = "کشتی",
        ["Wagons"] = "واگون‌ها",
        ["Wagon"] = "واگون",
        ["Reports"] = "گزارش‌ها",
        ["Reconciliation"] = "کنترل و آشتی"
    };

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Price"] = "قیمت",
        ["UnitPrice"] = "قیمت",
        ["PriceUsd"] = "قیمت دالر",
        ["Quantity"] = "مقدار",
        ["QuantityMt"] = "مقدار",
        ["LoadedQuantity"] = "مقدار بارگیری",
        ["ReceivedQuantity"] = "مقدار رسید",
        ["Amount"] = "مبلغ",
        ["AmountUsd"] = "مبلغ دالر",
        ["Rate"] = "نرخ",
        ["FxRate"] = "نرخ ارز",
        ["FxRateToUsd"] = "نرخ ارز",
        ["RoleId"] = "نقش",
        ["CanManageData"] = "دسترسی ثبت و ویرایش",
        ["CanManageUsers"] = "دسترسی مدیریت کاربران",
        ["AllowedNavigationItems"] = "بخش‌های قابل نمایش",
        ["IsCancelled"] = "وضعیت لغو",
        ["IsArchived"] = "وضعیت آرشیف",
        ["Status"] = "وضعیت"
    };

    private static readonly Dictionary<string, string> EntityControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Contract"] = "Contracts",
        ["ContractAmendment"] = "ContractAmendments",
        ["LoadingRegister"] = "Loading",
        ["LoadingReceipt"] = "LoadingReceipts",
        ["Dispatch"] = "Dispatch",
        ["SalesTransaction"] = "Sales",
        ["Sale"] = "Sales",
        ["PaymentTransaction"] = "Payments",
        ["Payment"] = "Payments",
        ["LedgerEntry"] = "Ledger",
        ["DailyFxRate"] = "DailyFxRates",
        ["DailyPlattsPrice"] = "PlattsRates",
        ["PlattsMonthlyManual"] = "PlattsRates",
        ["ExpenseTransaction"] = "Expenses",
        ["Expense"] = "Expenses",
        ["LossEvent"] = "LossEvents",
        ["CashAccount"] = "CashAccounts",
        ["Role"] = "Roles",
        ["User"] = "Users",
        ["StorageTank"] = "StorageTanks",
        ["Truck"] = "Trucks",
        ["Driver"] = "Drivers",
        ["Vessel"] = "Vessels",
        ["Wagon"] = "Wagons",
        ["Product"] = "Products",
        ["InventoryMovement"] = "Inventory"
    };

    private readonly ApplicationDbContext _db;

    public AuditLogsController(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// فیلتر واحد فهرست ممیزی. صفحهٔ Index و خروجی هر دو از همین متد می‌خوانند تا
    /// هیچ‌وقت فیلتر صفحه و فیلتر خروجی از هم جدا نشوند.
    /// </summary>
    private IQueryable<AuditLog> BuildFilteredQuery(
        string? q,
        string? user,
        string? category,
        string? module,
        string? action,
        string? severity,
        string? success,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var normalizedFromUtc = NormalizeUtc(fromUtc);
        var normalizedToUtc = NormalizeUtc(toUtc);
        var normalizedSeverity = NormalizeSeverity(severity);
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var trimmed = q.Trim();
            var hasNumericQuery = int.TryParse(trimmed, out var numericQuery);
            query = query.Where(log =>
                log.EntityName.Contains(trimmed)
                || (log.Module != null && log.Module.Contains(trimmed))
                || (log.Description != null && log.Description.Contains(trimmed))
                || (log.ActorUsername != null && log.ActorUsername.Contains(trimmed))
                || (log.RequestPath != null && log.RequestPath.Contains(trimmed))
                || (log.ControllerName != null && log.ControllerName.Contains(trimmed))
                || (log.ActionName != null && log.ActionName.Contains(trimmed))
                || (log.Diff != null && log.Diff.Contains(trimmed))
                || (log.MetadataJson != null && log.MetadataJson.Contains(trimmed))
                || (hasNumericQuery && log.EntityId == numericQuery));
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var trimmedUser = user.Trim();
            if (int.TryParse(trimmedUser, out var actorUserId))
            {
                query = query.Where(log => log.ActorUserId == actorUserId || log.ActorUsername == trimmedUser);
            }
            else
            {
                query = query.Where(log => log.ActorUsername == trimmedUser);
            }
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(log => log.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(module))
        {
            query = query.Where(log => log.Module == module);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(log => log.Action == action);
        }

        if (string.Equals(success, "true", StringComparison.OrdinalIgnoreCase))
        {
                query = query.Where(log => log.IsSuccess);
        }
        else if (string.Equals(success, "false", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(log => !log.IsSuccess);
        }

        query = ApplySeverityFilter(query, normalizedSeverity);

        if (normalizedFromUtc.HasValue)
        {
            var fromInclusiveUtc = StartOfDayUtc(normalizedFromUtc.Value);
            query = query.Where(log => log.ActionAtUtc >= fromInclusiveUtc);
        }

        if (normalizedToUtc.HasValue)
        {
            var toExclusiveUtc = StartOfDayUtc(normalizedToUtc.Value).AddDays(1);
            query = query.Where(log => log.ActionAtUtc < toExclusiveUtc);
        }

        return query;
    }

    public async Task<IActionResult> Index(
        string? q = null,
        string? user = null,
        string? category = null,
        string? module = null,
        [FromQuery] string? action = null,
        string? severity = null,
        string? success = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int? limit = null,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        _ = limit;

        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        // همان مقادیر نرمال‌شده که در فیلتر به کار می‌روند، برای نمایش در فرم هم لازم‌اند.
        var normalizedFromUtc = NormalizeUtc(fromUtc);
        var normalizedToUtc = NormalizeUtc(toUtc);
        var normalizedSeverity = NormalizeSeverity(severity);
        var query = BuildFilteredQuery(q, user, category, module, action, severity, success, fromUtc, toUtc);

        var counts = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                SuccessCount = g.Count(log => log.IsSuccess),
            })
            .FirstOrDefaultAsync();
        var totalCount = counts?.TotalCount ?? 0;
        var successCount = counts?.SuccessCount ?? 0;
        var sensitiveCount = await WhereSensitive(query).CountAsync();
        var activeUserCount = await query
            .Where(log => log.ActorUsername != null && log.ActorUsername != "")
            .Select(log => log.ActorUsername!)
            .Distinct()
            .CountAsync();
        var lastActivityAtUtc = await query
            .Select(log => (DateTime?)log.ActionAtUtc)
            .MaxAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var rawItems = await query
            .OrderByDescending(log => log.ActionAtUtc)
            .ThenByDescending(log => log.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = rawItems.Select(ToListItemViewModel).ToList();

        var users = await _db.AuditLogs
            .AsNoTracking()
            .Where(log => log.ActorUsername != null && log.ActorUsername != "")
            .Select(log => log.ActorUsername!)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync();
        var categories = await _db.AuditLogs
            .AsNoTracking()
            .Select(log => log.Category)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync();
        var modules = await _db.AuditLogs
            .AsNoTracking()
            .Where(log => log.Module != null)
            .Select(log => log.Module!)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync();
        var actions = await _db.AuditLogs
            .AsNoTracking()
            .Select(log => log.Action)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync();

        ViewData["Categories"] = categories;
        ViewData["HideSectionTabs"] = true;

        return View(new ActivityLogIndexViewModel
        {
            Filter = new ActivityLogFilterViewModel
            {
                Query = q,
                User = user,
                Category = category,
                Module = module,
                Action = action,
                Severity = normalizedSeverity,
                Success = success,
                FromUtc = normalizedFromUtc,
                ToUtc = normalizedToUtc,
                Limit = pageSize,
                Page = page,
            },
            Items = items,
            TotalCount = totalCount,
            SensitiveCount = sensitiveCount,
            SuccessCount = successCount,
            FailedCount = totalCount - successCount,
            ActiveUserCount = activeUserCount,
            LastActivityAtUtc = lastActivityAtUtc,
            CurrentPage = page,
            PageCount = pageCount,
            Users = users,
            Modules = modules,
            Actions = actions,
            SeverityOptions = SeverityOptions,
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var log = await _db.AuditLogs
            .AsNoTracking()
            .Where(item => item.Id == id)
            .FirstOrDefaultAsync();

        if (log is null)
        {
            return NotFound();
        }

        var actorRoleName = log.ActorUserId.HasValue
            ? await _db.Users
                .AsNoTracking()
                .Where(user => user.Id == log.ActorUserId.Value)
                .Select(user => user.Role != null ? user.Role.Name : null)
                .FirstOrDefaultAsync()
            : null;

        return View(ToDetailsViewModel(log, actorRoleName));
    }

    private static IQueryable<AuditLog> ApplySeverityFilter(IQueryable<AuditLog> query, string? severity)
        => severity switch
        {
            "error" => WhereError(query),
            "security" => WhereSecurity(query),
            "sensitive" => WhereSensitive(query),
            "normal" => WhereNormal(query),
            _ => query
        };

    private static IQueryable<AuditLog> WhereError(IQueryable<AuditLog> query)
        => query.Where(log => !log.IsSuccess || (log.StatusCode.HasValue && log.StatusCode.Value >= 400));

    private static IQueryable<AuditLog> WhereSecurity(IQueryable<AuditLog> query)
        => query.Where(log =>
            log.Category == AuditLogCategories.Security
            || log.Category == AuditLogCategories.Authentication
            || (log.Action != null && (log.Action.Contains("LoginFailed") || log.Action.Contains("Unauthorized") || log.Action.Contains("Forbidden")))
            || (log.Description != null && (log.Description.Contains("Unauthorized") || log.Description.Contains("Forbidden") || log.Description.Contains("AccessDenied"))));

    private static IQueryable<AuditLog> WhereSensitive(IQueryable<AuditLog> query)
        => query.Where(log =>
            SensitiveEntityNames.Contains(log.EntityName)
            || (log.Module != null && SensitiveModules.Contains(log.Module))
            || SensitiveActions.Contains(log.Action)
            || (log.HttpMethod != null && log.HttpMethod == "DELETE")
            || (log.Diff != null && (
                log.Diff.Contains("Price")
                || log.Diff.Contains("Rate")
                || log.Diff.Contains("Fx")
                || log.Diff.Contains("Quantity")
                || log.Diff.Contains("Amount")
                || log.Diff.Contains("Ledger")
                || log.Diff.Contains("Role")
                || log.Diff.Contains("Access")
                || log.Diff.Contains("IsCancelled")
                || log.Diff.Contains("IsArchived")
                || log.Diff.Contains("Status"))));

    private static IQueryable<AuditLog> WhereNormal(IQueryable<AuditLog> query)
        => query.Where(log =>
            log.IsSuccess
            && (!log.StatusCode.HasValue || log.StatusCode.Value < 400)
            && log.Category != AuditLogCategories.Security
            && log.Category != AuditLogCategories.Authentication
            && !SensitiveEntityNames.Contains(log.EntityName)
            && (log.Module == null || !SensitiveModules.Contains(log.Module))
            && !SensitiveActions.Contains(log.Action)
            && (log.HttpMethod == null || log.HttpMethod != "DELETE")
            && (log.Diff == null || !(
                log.Diff.Contains("Price")
                || log.Diff.Contains("Rate")
                || log.Diff.Contains("Fx")
                || log.Diff.Contains("Quantity")
                || log.Diff.Contains("Amount")
                || log.Diff.Contains("Ledger")
                || log.Diff.Contains("Role")
                || log.Diff.Contains("Access")
                || log.Diff.Contains("IsCancelled")
                || log.Diff.Contains("IsArchived")
                || log.Diff.Contains("Status"))));

    private static ActivityLogListItemViewModel ToListItemViewModel(AuditLog log)
    {
        var severity = DetermineSeverity(log);
        var related = ResolveRelatedRecord(log);

        return new ActivityLogListItemViewModel
        {
            Id = log.Id,
            ActionAtUtc = log.ActionAtUtc,
            Category = log.Category,
            Action = log.Action,
            EntityName = log.EntityName,
            EntityId = log.EntityId,
            Module = log.Module,
            Description = log.Description,
            ActorUsername = log.ActorUsername,
            ActorUserId = log.ActorUserId,
            HttpMethod = log.HttpMethod,
            RequestPath = log.RequestPath,
            StatusCode = log.StatusCode,
            IsSuccess = log.IsSuccess,
            DurationMs = log.DurationMs,
            ActorDisplay = ActorDisplay(log),
            ModuleDisplay = ModuleLabel(log.Module, log.EntityName),
            ActionDisplay = ActionLabel(log),
            RelatedRecord = related.Label,
            HumanSummary = BuildHumanSummary(log),
            Severity = severity,
            SeverityLabel = SeverityLabel(severity),
            SeverityCssClass = SeverityCssClass(severity),
            ResultLabel = ResultLabel(log),
            ResultCssClass = log.IsSuccess ? "status-badge-success" : "status-badge-danger",
            RelatedController = related.Controller,
            RelatedAction = related.Action,
            RelatedId = related.Id
        };
    }

    private static ActivityLogDetailsViewModel ToDetailsViewModel(AuditLog log, string? actorRoleName)
    {
        var severity = DetermineSeverity(log);
        var related = ResolveRelatedRecord(log);

        return new ActivityLogDetailsViewModel
        {
            Id = log.Id,
            ActionAtUtc = log.ActionAtUtc,
            Category = log.Category,
            Action = log.Action,
            EntityName = log.EntityName,
            EntityId = log.EntityId,
            Module = log.Module,
            Description = log.Description,
            Diff = log.Diff,
            ActorUsername = log.ActorUsername,
            ActorUserId = log.ActorUserId,
            ActorRoleName = actorRoleName,
            HttpMethod = log.HttpMethod,
            RequestPath = log.RequestPath,
            ControllerName = log.ControllerName,
            ActionName = log.ActionName,
            StatusCode = log.StatusCode,
            IsSuccess = log.IsSuccess,
            CorrelationId = log.CorrelationId,
            IpAddress = log.IpAddress,
            UserAgent = log.UserAgent,
            DurationMs = log.DurationMs,
            MetadataJson = log.MetadataJson,
            CreatedAtUtc = log.CreatedAtUtc,
            UpdatedAtUtc = log.UpdatedAtUtc,
            ActorDisplay = ActorDisplay(log),
            ModuleDisplay = ModuleLabel(log.Module, log.EntityName),
            ActionDisplay = ActionLabel(log),
            RelatedRecord = related.Label,
            HumanSummary = BuildHumanSummary(log),
            Severity = severity,
            SeverityLabel = SeverityLabel(severity),
            SeverityCssClass = SeverityCssClass(severity),
            ResultLabel = ResultLabel(log),
            ResultCssClass = log.IsSuccess ? "status-badge-success" : "status-badge-danger",
            RelatedController = related.Controller,
            RelatedAction = related.Action,
            RelatedId = related.Id,
            Changes = ParseDiff(log.Diff)
        };
    }

    private static string NormalizeSeverity(string? severity)
    {
        var value = severity?.Trim().ToLowerInvariant();
        return SeverityOptions.Any(option => option.Value == value) ? value! : "";
    }

    private static string DetermineSeverity(AuditLog log)
    {
        if (!log.IsSuccess || (log.StatusCode.HasValue && log.StatusCode.Value >= 400))
            return "error";

        if (IsSecurityLog(log))
            return "security";

        return IsSensitiveLog(log) ? "sensitive" : "normal";
    }

    private static bool IsSecurityLog(AuditLog log)
        => string.Equals(log.Category, AuditLogCategories.Security, StringComparison.OrdinalIgnoreCase)
            || string.Equals(log.Category, AuditLogCategories.Authentication, StringComparison.OrdinalIgnoreCase)
            || ContainsAny(log.Action, "LoginFailed", "Unauthorized", "Forbidden", "AccessDenied")
            || ContainsAny(log.Description, "Unauthorized", "Forbidden", "AccessDenied");

    private static bool IsSensitiveLog(AuditLog log)
        => SensitiveEntityNames.Contains(log.EntityName, StringComparer.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(log.Module) && SensitiveModules.Contains(log.Module, StringComparer.OrdinalIgnoreCase))
            || SensitiveActions.Contains(log.Action, StringComparer.OrdinalIgnoreCase)
            || string.Equals(log.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(log.Diff, "Price", "Rate", "Fx", "Quantity", "Amount", "Ledger", "Role", "Access", "IsCancelled", "IsArchived", "Status");

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string SeverityLabel(string severity)
        => severity switch
        {
            "error" => "خطا",
            "security" => "امنیتی",
            "sensitive" => "حساس",
            _ => "عادی"
        };

    private static string SeverityCssClass(string severity)
        => severity switch
        {
            "error" => "audit-severity-error",
            "security" => "audit-severity-security",
            "sensitive" => "audit-severity-sensitive",
            _ => "audit-severity-normal"
        };

    private static string ResultLabel(AuditLog log)
    {
        if (!log.IsSuccess)
            return "ناموفق";

        return log.StatusCode.HasValue && log.StatusCode.Value >= 400 ? "خطا" : "موفق";
    }

    private static string ActorDisplay(AuditLog log)
        => !string.IsNullOrWhiteSpace(log.ActorUsername)
            ? log.ActorUsername.Trim()
            : log.ActorUserId.HasValue
                ? $"کاربر #{log.ActorUserId.Value}"
                : "سیستم";

    private static string ActorPhrase(AuditLog log)
        => string.Equals(ActorDisplay(log), "سیستم", StringComparison.Ordinal)
            ? "سیستم"
            : $"کاربر {ActorDisplay(log)}";

    private static string ModuleLabel(string? module, string? entityName = null)
    {
        var key = !string.IsNullOrWhiteSpace(module) ? module : entityName;
        if (string.IsNullOrWhiteSpace(key))
            return "سیستم";

        return ModuleLabels.TryGetValue(key.Trim(), out var label) ? label : key.Trim();
    }

    private static string FieldLabel(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return "فیلد";

        var key = field.Trim();
        return FieldLabels.TryGetValue(key, out var label) ? label : key;
    }

    private static string ActionLabel(AuditLog log)
    {
        var action = log.Action?.Trim() ?? "";
        var method = log.HttpMethod?.Trim().ToUpperInvariant();

        return action.ToLowerInvariant() switch
        {
            "insert" or "create" => "ثبت",
            "update" or "edit" => "ویرایش",
            "delete" or "remove" => "حذف",
            "reverse" => "برگشت",
            "approve" => "تایید",
            "login" or "signin" or "loginsuccess" => "ورود",
            "loginfailed" => "ورود ناموفق",
            "logout" or "signout" => "خروج",
            "get" => "مشاهده",
            "post" => "ثبت",
            "put" or "patch" => "ویرایش",
            _ when method == "GET" => "مشاهده",
            _ when method == "POST" => "ثبت",
            _ when method == "DELETE" => "حذف",
            _ => string.IsNullOrWhiteSpace(action) ? "-" : action
        };
    }

    private static string BuildHumanSummary(AuditLog log)
    {
        if (!string.IsNullOrWhiteSpace(log.Description) && !IsLowValueRequestDescription(log.Description))
            return log.Description.Trim();

        var actor = ActorPhrase(log);
        var module = ModuleLabel(log.Module, log.EntityName);
        var entity = ModuleLabel(log.EntityName);
        var record = log.EntityId > 0 ? $" #{log.EntityId}" : "";
        var action = log.Action?.Trim().ToLowerInvariant() ?? "";

        if (string.Equals(log.Category, AuditLogCategories.Authentication, StringComparison.OrdinalIgnoreCase))
        {
            return log.IsSuccess
                ? $"{actor} وارد سیستم شد."
                : $"{actor} تلاش ناموفق برای ورود داشت.";
        }

        if (string.Equals(log.Category, AuditLogCategories.Security, StringComparison.OrdinalIgnoreCase))
            return $"{actor} رویداد امنیتی در بخش {module} ایجاد کرد.";

        if (action is "update" or "edit")
        {
            var change = ParseDiff(log.Diff).FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Before)
                && !string.IsNullOrWhiteSpace(item.After)
                && item.Before != "-"
                && item.After != "-");

            if (change is not null)
                return $"{actor} {FieldLabel(change.Field)} {entity}{record} را از {change.Before} به {change.After} تغییر داد.";

            return $"{actor} {entity}{record} را ویرایش کرد.";
        }

        if (action is "insert" or "create" or "post")
            return $"{actor} {entity}{record} را ثبت کرد.";

        if (action is "delete" or "remove")
            return $"{actor} {entity}{record} را حذف کرد.";

        if (action is "reverse")
            return $"{actor} {entity}{record} را برگشت داد.";

        if (action is "approve")
            return $"{actor} {entity}{record} را تایید کرد.";

        if (string.Equals(log.Category, AuditLogCategories.Request, StringComparison.OrdinalIgnoreCase))
        {
            var method = (log.HttpMethod ?? log.Action ?? "").ToUpperInvariant();
            return method switch
            {
                "GET" => $"{actor} بخش {module} را مشاهده کرد.",
                "POST" or "PUT" or "PATCH" => $"{actor} در بخش {module} اطلاعات ثبت/ویرایش کرد.",
                "DELETE" => $"{actor} در بخش {module} حذف انجام داد.",
                _ => $"{actor} در بخش {module} فعالیت داشت."
            };
        }

        return $"{actor} در بخش {module} فعالیت داشت.";
    }

    private static bool IsLowValueRequestDescription(string description)
        => description.StartsWith("GET ", StringComparison.OrdinalIgnoreCase)
            || description.StartsWith("POST ", StringComparison.OrdinalIgnoreCase)
            || description.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase)
            || description.StartsWith("PATCH ", StringComparison.OrdinalIgnoreCase)
            || description.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase);

    private static (string? Controller, string Action, int? Id, string Label) ResolveRelatedRecord(AuditLog log)
    {
        var label = log.EntityId > 0
            ? $"{ModuleLabel(log.EntityName)} #{log.EntityId}"
            : "-";

        if (log.EntityId <= 0)
            return (null, "Details", null, label);

        if (EntityControllers.TryGetValue(log.EntityName, out var controller)
            || (!string.IsNullOrWhiteSpace(log.Module) && EntityControllers.TryGetValue(log.Module, out controller)))
        {
            return (controller, "Details", log.EntityId, label);
        }

        return (null, "Details", null, label);
    }

    private static IReadOnlyList<ActivityLogChangeViewModel> ParseDiff(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
            return Array.Empty<ActivityLogChangeViewModel>();

        var text = diff.Trim();
        var mode = "";
        var body = text;
        var prefixIndex = text.IndexOf(':');
        if (prefixIndex > 0)
        {
            mode = text[..prefixIndex].Trim();
            body = text[(prefixIndex + 1)..].Trim();
        }

        var rows = new List<ActivityLogChangeViewModel>();
        foreach (var segment in body.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Equals("no field-level changes", StringComparison.OrdinalIgnoreCase))
                continue;

            var arrowIndex = segment.IndexOf(" -> ", StringComparison.Ordinal);
            var colonIndex = segment.IndexOf(':');
            if (arrowIndex > 0 && colonIndex > 0 && colonIndex < arrowIndex)
            {
                rows.Add(new ActivityLogChangeViewModel
                {
                    Field = FieldLabel(segment[..colonIndex]),
                    Before = segment[(colonIndex + 1)..arrowIndex].Trim(),
                    After = segment[(arrowIndex + 4)..].Trim()
                });
                continue;
            }

            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex > 0)
            {
                var value = segment[(equalsIndex + 1)..].Trim();
                rows.Add(new ActivityLogChangeViewModel
                {
                    Field = FieldLabel(segment[..equalsIndex]),
                    Before = string.Equals(mode, "Delete", StringComparison.OrdinalIgnoreCase) ? value : "-",
                    After = string.Equals(mode, "Delete", StringComparison.OrdinalIgnoreCase) ? "-" : value
                });
            }
        }

        return rows;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue ? NormalizeUtc(value.Value) : null;

    private static DateTime StartOfDayUtc(DateTime value)
        => new(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);
}
