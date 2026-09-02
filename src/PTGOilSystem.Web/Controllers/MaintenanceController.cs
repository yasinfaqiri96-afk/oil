using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize(Policy = AuthPolicies.AdminOnly)]
public sealed class MaintenanceController : Controller
{
    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private readonly IConfiguration _configuration;
    private readonly AuthBootstrapper _bootstrapper;

    public MaintenanceController(ApplicationDbContext db, IConfiguration configuration, AuthBootstrapper bootstrapper)
    {
        _db = db;
        _configuration = configuration;
        _bootstrapper = bootstrapper;
    }

    [HttpPost]
    [Route("/maintenance/clear-data-except-users")]
    public async Task<IActionResult> ClearDataExceptUsers()
    {
        if (!IsResetEnabled())
        {
            return NotFound("Database reset is disabled.");
        }

        if (!_db.Database.IsRelational())
        {
            return BadRequest("This reset flow only supports relational databases.");
        }

        var tables = _db.Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() is not null)
            .Select(entityType => new TableDescriptor(entityType.GetSchema() ?? "public", entityType.GetTableName()!))
            .Where(table => !string.Equals(table.Table, "Users", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(table => $"{table.Schema}.{table.Table}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Table, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tables.Count == 0)
        {
            return BadRequest("No tables were found to truncate.");
        }

        var temporaryForeignKeys = await DropPreservedUserForeignKeysAsync(tables);
        try
        {
            var truncateSql = BuildTruncateSql(tables);
            await _db.Database.ExecuteSqlRawAsync(truncateSql);

            await _bootstrapper.EnsureDefaultRolesAsync();

            return Ok(new
            {
                message = "All non-user data was cleared successfully.",
                preservedTable = "Users",
                truncatedTables = tables.Count,
                sql = truncateSql
            });
        }
        finally
        {
            await RestorePreservedUserForeignKeysAsync(temporaryForeignKeys);
        }
    }

    // Backfill: دیسپچ‌های وصل‌به‌رسید (مثل انتقال گروهی واگن→موتر) که کرایه‌شان تسویه شده ولی
    // به‌خاطر early-returnِ قدیمی مصرف/لجرِ کرایه نساخته بودند، پس در سود‌وزیانِ پرونده محموله دیده نمی‌شدند.
    // DispatchFreightExpenseSync.SyncAsync خودش idempotent است و کرایهٔ خودِ رسید را دوباره نمی‌شمارد؛
    // اجرای چندباره امن است.
    [HttpPost]
    [Route("/maintenance/backfill-dispatch-freight-expenses")]
    public async Task<IActionResult> BackfillDispatchFreightExpenses()
    {
        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.InventoryTransportReceiptId.HasValue
                && d.Status != DispatchStatus.Cancelled
                && (d.FreightPayableUsd > 0m || d.FreightCostUsd > 0m))
            .ToListAsync();

        foreach (var dispatch in dispatches)
        {
            await DispatchFreightExpenseSync.SyncAsync(_db, dispatch);
        }

        return Ok(new
        {
            message = "Dispatch freight expenses backfilled.",
            candidates = dispatches.Count
        });
    }

    // Backfill: بارگیری‌های دالریِ قیمت‌دارِ قرارداد خرید که پیش از پشتیبانی USD در SupplierLoadingLedger
    // ثبت شده‌اند و هیچ سطر بدهی تأمین‌کننده ندارند؛ به همین دلیل مانده و صورت‌حساب تأمین‌کننده صفر بود.
    // پیش‌فرض Dry Run است و چیزی نمی‌نویسد؛ نوشتن فقط با commit=true و داخل تراکنش انجام می‌شود.
    // ضدتکرار: هر بارگیری که از قبل سطر (SourceType=Loading, SourceId=Id) دارد کنار گذاشته می‌شود،
    // پس اجرای دوباره صفر رکورد می‌سازد. بارگیری روبلی از این مسیر عبور نمی‌کند.
    [HttpPost]
    [Route("/maintenance/backfill-supplier-usd-loading-ledger")]
    public async Task<IActionResult> BackfillSupplierUsdLoadingLedger(bool commit = false)
    {
        var loadings = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Where(l => l.Contract != null
                && l.Contract.ContractType == ContractType.Purchase
                && l.Contract.SupplierId != null
                && l.LoadedQuantityMt > 0m
                && l.LoadingPriceUsd != null
                && l.LoadingPriceUsd > 0m)
            .ToListAsync();

        var postedLoadingIds = (await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == SupplierLoadingLedger.SourceType)
                .Select(l => l.SourceId)
                .ToListAsync())
            .ToHashSet();

        var candidates = loadings
            .Where(l => !postedLoadingIds.Contains(l.Id))
            .Where(l => !LoadingRubSettlement.IsRubSettlement(l.SettlementCurrencyCode))
            .Where(l => SupplierLoadingLedger.IsPostable(l, l.Contract))
            .OrderBy(l => l.Id)
            .ToList();

        // حالت dry-run هیچ سطری نمی‌نویسد، پس فقط درخواست‌ها ساخته می‌شوند.
        var requests = candidates
            .Select(l => SupplierLoadingLedger.Create(l, l.Contract!))
            .ToList();
        var totalUsd = requests.Sum(e => e.AmountUsd);

        if (!commit)
        {
            return Ok(new
            {
                mode = "dryRun",
                message = "No row was written. Re-send with commit=true to apply.",
                candidates = candidates.Count,
                totalUsd,
                loadingIds = candidates.Select(l => l.Id).ToList()
            });
        }

        if (requests.Count == 0)
        {
            return Ok(new { mode = "commit", created = 0, totalUsd = 0m });
        }

        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            Ledger.PostRange([.. requests]);
            await _db.SaveChangesAsync();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return Ok(new
        {
            mode = "commit",
            created = requests.Count,
            totalUsd,
            loadingIds = candidates.Select(l => l.Id).ToList()
        });
    }

    private bool IsResetEnabled()
        => string.Equals(_configuration["PTG_ENABLE_DB_RESET"], "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(_configuration["PTG_ENABLE_DB_RESET"], "1", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ForeignKeyDefinition>> DropPreservedUserForeignKeysAsync(IEnumerable<TableDescriptor> tables)
    {
        var tableKeys = tables
            .Select(t => $"{t.Schema}.{t.Table}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var userEntityType = _db.Model.FindEntityType(typeof(User));
        if (userEntityType is null)
        {
            return Array.Empty<ForeignKeyDefinition>();
        }

        var foreignKeys = userEntityType.GetForeignKeys()
            .Where(foreignKey =>
            {
                var principalSchema = foreignKey.PrincipalEntityType.GetSchema() ?? "public";
                var principalTable = foreignKey.PrincipalEntityType.GetTableName();
                return principalTable is not null
                    && tableKeys.Contains($"{principalSchema}.{principalTable}");
            })
            .ToList();

        var definitions = new List<ForeignKeyDefinition>();
        foreach (var foreignKey in foreignKeys)
        {
            var definition = CreateForeignKeyDefinition(foreignKey);
            definitions.Add(definition);
            await _db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{EscapeIdentifier(definition.Schema)}\".\"{EscapeIdentifier(definition.Table)}\" DROP CONSTRAINT \"{EscapeIdentifier(definition.ConstraintName)}\";");
        }

        return definitions;
    }

    private async Task RestorePreservedUserForeignKeysAsync(IEnumerable<ForeignKeyDefinition> definitions)
    {
        foreach (var definition in definitions.Reverse())
        {
            await _db.Database.ExecuteSqlRawAsync(definition.RecreateSql);
        }
    }

    private static ForeignKeyDefinition CreateForeignKeyDefinition(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyForeignKey foreignKey)
    {
        var dependentSchema = foreignKey.DeclaringEntityType.GetSchema() ?? "public";
        var dependentTable = foreignKey.DeclaringEntityType.GetTableName()!;
        var principalSchema = foreignKey.PrincipalEntityType.GetSchema() ?? "public";
        var principalTable = foreignKey.PrincipalEntityType.GetTableName()!;
        var dependentColumns = string.Join(", ", foreignKey.Properties.Select(column => $"\"{EscapeIdentifier(column.GetColumnName())}\""));
        var principalColumns = string.Join(", ", foreignKey.PrincipalKey.Properties.Select(column => $"\"{EscapeIdentifier(column.GetColumnName())}\""));
        var constraintName = foreignKey.GetConstraintName() ?? $"FK_{dependentTable}_{principalTable}";

        var createSql = $"ALTER TABLE \"{EscapeIdentifier(dependentSchema)}\".\"{EscapeIdentifier(dependentTable)}\" ADD CONSTRAINT \"{EscapeIdentifier(constraintName)}\" FOREIGN KEY ({dependentColumns}) REFERENCES \"{EscapeIdentifier(principalSchema)}\".\"{EscapeIdentifier(principalTable)}\" ({principalColumns}) ON DELETE NO ACTION ON UPDATE NO ACTION;";

        return new ForeignKeyDefinition(dependentSchema, dependentTable, constraintName, createSql);
    }

    private static string BuildTruncateSql(IEnumerable<TableDescriptor> tables)
        => $"TRUNCATE TABLE {string.Join(", ", tables.Select(table => $"\"{EscapeIdentifier(table.Schema)}\".\"{EscapeIdentifier(table.Table)}\""))} RESTART IDENTITY CASCADE;";

    private static string EscapeIdentifier(string identifier)
        => identifier.Replace("\"", "\"\"");

    private sealed record TableDescriptor(string Schema, string Table);
    private sealed record ForeignKeyDefinition(string Schema, string Table, string ConstraintName, string RecreateSql);

    // Backfill: کلیدِ جستجوی canonical برای سطرهای پیش از این تغییر (SearchKey خالی).
    // متنِ نمایشی دست نمی‌خورد؛ فقط ستونِ کمکی پر می‌شود. اجرای دوباره امن است چون کلید
    // همیشه از همان متن ساخته می‌شود. پیش‌فرض Dry Run است. برخوردِ کلید فقط گزارش
    // می‌شود — هیچ سطری ادغام یا حذف نمی‌شود.
    [HttpPost]
    [Route("/maintenance/backfill-canonical-search-keys")]
    public async Task<IActionResult> BackfillCanonicalSearchKeys(bool commit = false, CancellationToken cancellationToken = default)
    {
        var result = await CanonicalSearchKeyBackfill.RunAsync(_db, commit, cancellationToken);

        return Ok(new
        {
            message = commit ? "Canonical search keys backfilled." : "Dry run only. Pass commit=true to write.",
            committed = result.Committed,
            totalUpdated = result.TotalUpdated,
            totalCollisions = result.TotalCollisions,
            tables = result.Tables
        });
    }
}
