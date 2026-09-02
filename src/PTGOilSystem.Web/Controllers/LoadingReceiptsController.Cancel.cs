using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.LoadingReceipts;

namespace PTGOilSystem.Web.Controllers;

// لغو رسید بارگیری. رکورد حذف نمی‌شود؛ اثرهای موجودی/مالی با سند معکوس برمی‌گردند و
// وابستگی‌های پایین‌دستی پیش از هر تغییر بررسی می‌شوند (همه یا هیچ).
public partial class LoadingReceiptsController
{
    private ILoadingReceiptCancellationService Cancellation
        => _cancellation ?? new LoadingReceiptCancellationService(
            _db,
            _audit,
            NullLogger<LoadingReceiptCancellationService>.Instance);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Cancel(int id, string? reason, uint? rowVersion = null, string? returnUrl = null)
        => CancelInternalAsync([id], reason, rowVersion, returnUrl);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> BulkCancel(int[]? ids, string? reason, string? returnUrl = null)
        => CancelInternalAsync(ids ?? [], reason, rowVersion: null, returnUrl);

    private async Task<IActionResult> CancelInternalAsync(
        IReadOnlyCollection<int> receiptIds,
        string? reason,
        uint? rowVersion,
        string? returnUrl)
    {
        var ids = receiptIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            TempData["err"] = "هیچ رسیدی برای لغو انتخاب نشده است.";
            return CancelRedirect(returnUrl, ids.FirstOrDefault());
        }

        var normalizedReason = (reason ?? string.Empty).Trim();
        if (normalizedReason.Length == 0)
        {
            TempData["err"] = "ثبت دلیل لغو الزامی است.";
            return CancelRedirect(returnUrl, ids[0]);
        }

        // بررسی نسخهٔ سطر برای لغو تکی: اگر رسید در این فاصله تغییر کرده باشد، لغو انجام نمی‌شود.
        if (rowVersion.HasValue && ids.Count == 1)
        {
            var currentRowVersion = await _db.LoadingReceipts
                .AsNoTracking()
                .Where(r => r.Id == ids[0])
                .Select(r => (uint?)r.RowVersion)
                .FirstOrDefaultAsync();

            if (currentRowVersion is null)
            {
                return NotFound();
            }

            if (currentRowVersion.Value != rowVersion.Value)
            {
                TempData["err"] = "این رسید هم‌زمان توسط کاربر دیگری تغییر کرده است؛ صفحه را تازه کنید و دوباره تلاش کنید.";
                return CancelRedirect(returnUrl, ids[0]);
            }
        }

        try
        {
            var result = await Cancellation.CancelAsync(ids, normalizedReason, _currentUser?.UserId);

            if (!result.Succeeded)
            {
                TempData["err"] = BuildBlockerMessage(result.Blockers);
                return CancelRedirect(returnUrl, ids[0]);
            }

            TempData["ok"] = result.CancelledReceiptIds.Count == 1
                ? $"رسید #{result.CancelledReceiptIds[0]} لغو شد و اثرهای موجودی و مالی آن برگشت."
                : $"{result.CancelledReceiptIds.Count} رسید لغو شد و اثرهای موجودی و مالی آن‌ها برگشت.";
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["err"] = "این رسید هم‌زمان توسط کاربر دیگری تغییر کرده است؛ صفحه را تازه کنید و دوباره تلاش کنید.";
        }
        catch (Services.Exceptions.BusinessRuleException exception)
        {
            TempData["err"] = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to cancel loading receipts {ReceiptIds}.", string.Join(",", ids));
            TempData["err"] = "لغو رسید انجام نشد؛ هیچ تغییری ثبت نشد.";
        }

        return CancelRedirect(returnUrl, ids[0]);
    }

    /// <summary>
    /// «اصلاح مقدار»: فرم ثبت رسید با مقادیر رسید قبلی باز می‌شود. هنگام ثبت، رسید قبلی در همان
    /// تراکنش لغو و رسید جدید ثبت می‌شود؛ هیچ فیلد اثرگذاری با Update ساده تغییر نمی‌کند.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Correct(int id, string? returnUrl = null)
    {
        var receipt = await _db.LoadingReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt is null)
        {
            return NotFound();
        }

        if (receipt.IsCancelled)
        {
            TempData["err"] = "این رسید قبلاً لغو شده است؛ برای ثبت مقدار درست، رسید جدید ثبت کنید.";
            return CancelRedirect(returnUrl, id);
        }

        var blockers = await Cancellation.InspectAsync([id]);
        if (blockers.Count > 0)
        {
            TempData["err"] = BuildBlockerMessage(blockers);
            return CancelRedirect(returnUrl, id);
        }

        var loadingContext = await GetLoadingContextAsync(receipt.LoadingRegisterId);
        if (loadingContext is null)
        {
            return NotFound();
        }

        var model = new Models.Loading.LoadingReceiptCreateViewModel
        {
            LoadingRegisterId = receipt.LoadingRegisterId,
            CorrectionOfReceiptId = receipt.Id,
            // رسید ترکیبی دیگر ساخته نمی‌شود. اصلاح = لغو + ثبت دوباره، پس رسید قدیمیِ
            // ترکیبی باید با یک مقصد مشخص دوباره ثبت شود؛ کاربر مقصد را روی فرم انتخاب می‌کند.
            ReceiptDestination = receipt.ReceiptDestination == Models.Entities.LoadingReceiptDestination.Mixed
                ? Models.Entities.LoadingReceiptDestination.ToInventory
                : receipt.ReceiptDestination,
            LossMode = receipt.LossMode,
            ReceiptDate = receipt.ReceiptDate,
            ArrivalDate = receipt.ArrivalDate,
            LeakDate = receipt.LeakDate,
            ActualArrivedQuantityMt = receipt.ActualArrivedQuantityMt,
            TerminalId = receipt.TerminalId,
            StorageTankId = receipt.StorageTankId,
            ReceivedQuantityMt = receipt.ReceivedQuantityMt,
            ReferenceDocument = receipt.ReferenceDocument,
            Notes = receipt.Notes,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null
        };

        // ظرفیت باقی‌مانده با احتساب همین رسید محاسبه می‌شود، چون هنگام ثبت لغو می‌شود.
        ApplyLoadingContext(model, loadingContext.Value.Loading, loadingContext.Value.Quantities);
        model.AlreadyReceivedQuantityMt = Math.Max(model.AlreadyReceivedQuantityMt - receipt.ReceivedQuantityMt, 0m);
        model.RemainingToReceiveMt += receipt.ReceivedQuantityMt;

        await PopulateLookupsAsync(model);
        return View("Create", model);
    }

    private static string BuildBlockerMessage(IReadOnlyList<LoadingReceiptCancellationBlocker> blockers)
        => "لغو انجام نشد. " + string.Join(" ", blockers.Select(b => b.Reason).Distinct());

    private IActionResult CancelRedirect(string? returnUrl, int receiptId)
    {
        if (TryGetLocalReturnUrl(returnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return receiptId > 0
            ? RedirectToAction(nameof(Details), new { id = receiptId })
            : RedirectToAction(nameof(Index));
    }
}
