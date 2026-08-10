using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «حذف گروهی بارگیری اشتباه» — راه بازگشت از یک ایمپورت اکسلِ غلط، بدون SQL دستی.
///
/// قاعدهٔ ایمنی: یک بارگیری فقط وقتی حذف می‌شود که هیچ کاری پایین‌دستش انجام نشده باشد.
/// هر رکوردی که وابسته است، ردیف را «قفل» می‌کند و دلیلش نمایش داده می‌شود:
///   • رسید بارگیری، اظهارنامهٔ گمرکی، کنترل کیفیت، رویداد ضایعات
///   • مصرف یا کرایهٔ دارایی که لغو نشده (چون لِجر/پرداخت به آن‌ها وصل است)
/// چیزی که «مالِ خودِ بارگیری» است و جای دیگری اثر ندارد، همراهش پاک می‌شود:
///   • سطرهای مصرف بارگیری (LoadingExpenseLine)
///   • سطر لِجر تأمین‌کننده (SourceType = "Loading") با سند معکوس خنثی می‌شود و حذف نمی‌شود
///   • سند دفتر کل جدید، که پیش از حذف با TryPostPurchaseReversalAsync برگردانده می‌شود
///
/// گاردها در POST دوباره از دیتابیس خوانده می‌شوند؛ به هیچ چیزی که از فرم می‌آید اعتماد نمی‌شود.
/// هر حذف یک ردیف Audit با AuditAction.Delete می‌گیرد.
/// </summary>
public partial class LoadingController
{
    private const int BulkDeleteMaxRows = 500;

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpGet]
    public async Task<IActionResult> BulkDelete(
        int? contractId = null,
        DateTime? importedFrom = null,
        DateTime? importedTo = null,
        bool onlyImported = true,
        string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        await PopulateBulkDeleteLookupsAsync(contractId, cancellationToken);

        // بدون انتخاب قرارداد هیچ ردیفی نشان داده نمی‌شود — حذف گروهی نباید با یک کلیک
        // ناخواسته کل جدول را جلوی کاربر بگذارد.
        var hasSearched = contractId.HasValue;
        var rows = hasSearched
            ? await BuildBulkDeleteRowsAsync(contractId!.Value, importedFrom, importedTo, onlyImported, cancellationToken)
            : [];

        return View(new LoadingBulkDeleteViewModel
        {
            ContractId = contractId,
            ContractNumber = await ResolveContractNumberAsync(contractId, cancellationToken),
            ImportedFrom = importedFrom,
            ImportedTo = importedTo,
            OnlyImported = onlyImported,
            HasSearched = hasSearched,
            Rows = rows,
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
        });
    }

    [Authorize(Policy = AuthPolicies.AdminOnly)]
    [HttpPost, ValidateAntiForgeryToken]
    [ActionName("BulkDelete")]
    public async Task<IActionResult> BulkDeleteConfirm(
        int[]? ids,
        int? contractId = null,
        DateTime? importedFrom = null,
        DateTime? importedTo = null,
        bool onlyImported = true,
        string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = (ids ?? []).Distinct().Where(id => id > 0).ToList();
        if (selectedIds.Count == 0)
        {
            TempData["error"] = "هیچ بارگیری‌ای برای حذف انتخاب نشده است.";
            return RedirectToAction(nameof(BulkDelete), new { contractId, importedFrom, importedTo, onlyImported, returnUrl });
        }

        var result = await DeleteLoadingsAsync(selectedIds, cancellationToken);

        if (result.DeletedCount > 0)
        {
            TempData["ok"] = $"{result.DeletedCount:N0} بارگیری حذف شد.";
        }

        if (result.SkippedCount > 0)
        {
            TempData["error"] = $"{result.SkippedCount:N0} بارگیری حذف نشد: "
                + string.Join(" ؛ ", result.SkippedMessages.Take(5))
                + (result.SkippedMessages.Count > 5 ? " ..." : string.Empty);
        }

        return RedirectToAction(nameof(BulkDelete), new { contractId, importedFrom, importedTo, onlyImported, returnUrl });
    }

    /// <summary>
    /// حذف واقعی. گاردها اینجا — نه در فرم — ارزیابی می‌شوند، و کل کار داخل یک تراکنش است
    /// تا یا همهٔ ردیف‌های مجاز پاک شوند یا هیچ‌کدام.
    /// </summary>
    private async Task<LoadingBulkDeleteResultViewModel> DeleteLoadingsAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        var loadings = await _db.LoadingRegisters
            .Include(l => l.Contract)
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(cancellationToken);

        var blockers = await LoadBlockersAsync(ids, cancellationToken);
        var skipped = new List<string>();
        var deletable = new List<LoadingRegister>();

        foreach (var id in ids)
        {
            var loading = loadings.FirstOrDefault(l => l.Id == id);
            if (loading is null)
            {
                skipped.Add($"#{id} یافت نشد");
                continue;
            }

            var reasons = blockers.TryGetValue(id, out var found) ? found : [];
            if (reasons.Count > 0)
            {
                skipped.Add($"#{id} ({string.Join("، ", reasons)})");
                continue;
            }

            deletable.Add(loading);
        }

        if (deletable.Count == 0)
        {
            return new LoadingBulkDeleteResultViewModel
            {
                DeletedCount = 0,
                SkippedCount = skipped.Count,
                SkippedMessages = skipped
            };
        }

        var deletableIds = deletable.Select(l => l.Id).ToList();

        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            // دفتر کل جدید پیش از حذف برگردانده می‌شود، چون Adapter به خودِ رکورد بارگیری نیاز دارد.
            if (_purchaseAccounting is not null)
            {
                foreach (var loading in deletable)
                {
                    await _purchaseAccounting.TryPostPurchaseReversalAsync(loading, cancellationToken);
                }
            }

            // Ledger قدیمی هم سند قطعی و قابل تفتیش است: اصل دست‌نخورده می‌ماند و
            // سطر جبرانی ثبت می‌شود. حذف خودِ بارگیری فقط به‌دلیل نبود پایین‌دست مجاز است.
            var ledgerEntries = await _db.LedgerEntries
                .Where(l => l.SourceType == SupplierLoadingLedgerSourceType && deletableIds.Contains(l.SourceId))
                .ToListAsync(cancellationToken);
            foreach (var ledger in ledgerEntries)
            {
                var reversal = await LedgerReversalWriter.ReverseAsync(
                    _db,
                    ledger,
                    _businessClock.Today,
                    $"Reversal for deleted erroneous loading #{ledger.SourceId}",
                    $"LOADING:{ledger.SourceId}",
                    cancellationToken);
                if (reversal is not null)
                {
                    await _audit.LogAsync(
                        nameof(LedgerEntry),
                        reversal.Id,
                        AuditAction.Reverse,
                        diff: AuditDiffFormatter.ForCreate(
                            ("ReversalOfLedgerEntryId", ledger.Id),
                            ("SourceType", reversal.SourceType),
                            ("SourceId", reversal.SourceId),
                            ("AmountUsd", reversal.AmountUsd)),
                        ct: cancellationToken);
                }
            }

            var expenseLines = await _db.LoadingExpenseLines
                .Where(x => deletableIds.Contains(x.LoadingRegisterId))
                .ToListAsync(cancellationToken);
            _db.LoadingExpenseLines.RemoveRange(expenseLines);

            foreach (var loading in deletable)
            {
                await _audit.LogAsync(
                    nameof(LoadingRegister),
                    loading.Id,
                    AuditAction.Delete,
                    diff: AuditDiffFormatter.ForDelete(
                        ("ContractId", loading.ContractId),
                        ("ContractNumber", loading.Contract?.ContractNumber),
                        ("LoadingDate", loading.LoadingDate),
                        ("LoadedQuantityMt", loading.LoadedQuantityMt),
                        ("LoadingPriceUsd", loading.LoadingPriceUsd),
                        ("BillOfLadingNumber", loading.BillOfLadingNumber),
                        ("WagonNumber", loading.WagonNumber),
                        ("ImportUniqueKey", loading.ImportUniqueKey)),
                    ct: cancellationToken);
            }

            _db.LoadingRegisters.RemoveRange(deletable);
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
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

        _logger.LogWarning(
            "Bulk delete removed {Count} loading registers: {Ids}",
            deletable.Count,
            string.Join(",", deletableIds));

        return new LoadingBulkDeleteResultViewModel
        {
            DeletedCount = deletable.Count,
            SkippedCount = skipped.Count,
            SkippedMessages = skipped
        };
    }

    private async Task<List<LoadingBulkDeleteRowViewModel>> BuildBulkDeleteRowsAsync(
        int contractId,
        DateTime? importedFrom,
        DateTime? importedTo,
        bool onlyImported,
        CancellationToken cancellationToken)
    {
        var query = _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId);

        if (onlyImported)
        {
            query = query.Where(l => l.ImportUniqueKey != null);
        }

        if (importedFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(importedFrom.Value, DateTimeKind.Utc);
            query = query.Where(l => l.CreatedAtUtc >= from);
        }

        if (importedTo.HasValue)
        {
            var to = DateTime.SpecifyKind(importedTo.Value, DateTimeKind.Utc);
            query = query.Where(l => l.CreatedAtUtc <= to);
        }

        var rows = await query
            .OrderByDescending(l => l.CreatedAtUtc)
            .ThenByDescending(l => l.Id)
            .Take(BulkDeleteMaxRows)
            .Select(l => new
            {
                l.Id,
                l.LoadingDate,
                l.CreatedAtUtc,
                l.TransportType,
                l.VesselId,
                l.TruckId,
                l.WagonNumber,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
                ProductName = l.Product != null ? l.Product.Name : "",
                VesselName = l.Vessel != null ? l.Vessel.Name : null,
                TruckPlateNumber = l.Truck != null ? l.Truck.PlateNumber : null,
                l.LoadedQuantityMt,
                l.LoadingPriceUsd,
                l.BillOfLadingNumber,
                l.ImportUniqueKey
            })
            .ToListAsync(cancellationToken);

        var blockers = await LoadBlockersAsync(rows.Select(r => r.Id).ToList(), cancellationToken);

        return rows
            .Select(r =>
            {
                var transportType = ResolveTransportType(r.TransportType, r.VesselId, r.TruckId, r.WagonNumber);
                return new LoadingBulkDeleteRowViewModel
                {
                    Id = r.Id,
                    LoadingDate = r.LoadingDate,
                    CreatedAtUtc = r.CreatedAtUtc,
                    ContractNumber = r.ContractNumber,
                    ProductName = r.ProductName,
                    VehicleSummary = BuildVehicleSummary(
                        transportType,
                        r.VesselName,
                        r.TruckPlateNumber,
                        r.WagonNumber),
                    BillOfLadingNumber = r.BillOfLadingNumber,
                    LoadedQuantityMt = r.LoadedQuantityMt,
                    LoadingValueUsd = CalculateLoadingValueUsd(r.LoadedQuantityMt, r.LoadingPriceUsd),
                    IsImported = r.ImportUniqueKey != null,
                    Blockers = blockers.TryGetValue(r.Id, out var found) ? found : []
                };
            })
            .ToList();
    }

    /// <summary>
    /// دلایل قفل‌شدن هر بارگیری، در یک رفت‌وبرگشت به‌ازای هر نوع وابستگی (نه به‌ازای هر ردیف).
    /// مصرف و کرایهٔ لغوشده قفل نمی‌کنند، چون دیگر اثر مالی ندارند.
    /// </summary>
    private async Task<Dictionary<int, List<string>>> LoadBlockersAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<string>>();
        if (ids.Count == 0)
        {
            return result;
        }

        void Add(IEnumerable<int> loadingIds, string reason)
        {
            foreach (var id in loadingIds)
            {
                if (!result.TryGetValue(id, out var list))
                {
                    list = [];
                    result[id] = list;
                }

                if (!list.Contains(reason))
                {
                    list.Add(reason);
                }
            }
        }

        Add(
            await _db.LoadingReceipts.AsNoTracking()
                .Where(x => ids.Contains(x.LoadingRegisterId) && !x.IsCancelled)
                .Select(x => x.LoadingRegisterId)
                .Distinct()
                .ToListAsync(cancellationToken),
            "رسید ثبت شده");

        Add(
            await _db.CustomsDeclarations.AsNoTracking()
                .Where(x => x.LoadingRegisterId != null && ids.Contains(x.LoadingRegisterId.Value))
                .Select(x => x.LoadingRegisterId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken),
            "اظهارنامه گمرکی");

        Add(
            await _db.QualityInspections.AsNoTracking()
                .Where(x => x.LoadingRegisterId != null && ids.Contains(x.LoadingRegisterId.Value))
                .Select(x => x.LoadingRegisterId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken),
            "کنترل کیفیت");

        Add(
            await _db.LossEvents.AsNoTracking()
                .Where(x => x.LoadingRegisterId != null
                    && !x.IsCancelled
                    && ids.Contains(x.LoadingRegisterId.Value))
                .Select(x => x.LoadingRegisterId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken),
            "رویداد ضایعات");

        Add(
            await _db.ExpenseTransactions.AsNoTracking()
                .Where(x => x.LoadingRegisterId != null
                    && !x.IsCancelled
                    && ids.Contains(x.LoadingRegisterId.Value))
                .Select(x => x.LoadingRegisterId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken),
            "مصرف ثبت‌شده");

        Add(
            await _db.AssetRentTransactions.AsNoTracking()
                .Where(x => x.LoadingRegisterId != null
                    && !x.IsCancelled
                    && ids.Contains(x.LoadingRegisterId.Value))
                .Select(x => x.LoadingRegisterId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken),
            "کرایه دارایی");

        return result;
    }

    private async Task<string?> ResolveContractNumberAsync(int? contractId, CancellationToken cancellationToken)
    {
        if (!contractId.HasValue) return null;

        var contract = await _db.Contracts.AsNoTracking()
            .Where(c => c.Id == contractId.Value)
            .Select(c => new { c.ContractName, c.ContractNumber })
            .FirstOrDefaultAsync(cancellationToken);
        return contract is null
            ? null
            : ContractUiText.FormatDisplayLabel(contract.ContractName, contract.ContractNumber);
    }

    private async Task PopulateBulkDeleteLookupsAsync(int? contractId, CancellationToken cancellationToken)
    {
        var contracts = await _db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .OrderByDescending(c => c.ContractDate)
                .Select(c => new { c.Id, c.ContractName, c.ContractNumber })
                .Take(LookupLimit)
                .ToListAsync(cancellationToken);
        ViewBag.BulkDeleteContracts = new SelectList(
            contracts.Select(c => new ContractLookupOption(
                c.Id,
                ContractUiText.FormatDisplayLabel(c.ContractName, c.ContractNumber))),
            "Id",
            "Display",
            contractId);
    }
}
