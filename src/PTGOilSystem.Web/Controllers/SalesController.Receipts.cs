using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

// تطبیقِ نقدِ دریافت مشتری با فروش (cash application).
//
// دریافتِ عادی مشتری سند خودش را همان لحظه گرفته است، پس این مسیر هیچ ژورنالی نمی‌زند و هیچ
// مانده‌ای را تکان نمی‌دهد؛ فقط ثبت می‌کند «کدام دریافت، چه مقدار، روی کدام فروش نشست». به همین
// دلیل فروش گروهی هم بدون ساختار تازه درست می‌شود: یک دریافت روی چند ردیف پخش می‌شود و سهم هر
// ردیف یک رکورد واقعیِ ذخیره‌شده است، نه یک تقسیمِ تناسبیِ حدسی.
public partial class SalesController
{
    private ICustomerReceiptApplicationService? _receiptApplications;

    private ICustomerReceiptApplicationService ReceiptApplications
        => _receiptApplications ??= new CustomerReceiptApplicationService(_db);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> ApplyReceipt(
        int paymentTransactionId,
        int[] saleIds,
        decimal[] amounts,
        string? returnUrl = null)
    {
        var lines = new List<CustomerReceiptApplicationLine>();
        for (var i = 0; i < saleIds.Length; i++)
        {
            var amount = i < amounts.Length ? amounts[i] : 0m;
            if (amount > 0m)
            {
                lines.Add(new CustomerReceiptApplicationLine(saleIds[i], amount));
            }
        }

        try
        {
            var created = await ReceiptApplications.ApplyAsync(new CustomerReceiptApplyRequest(
                paymentTransactionId, lines, User.Identity?.Name));
            TempData["ok"] = $"دریافت روی {created} فروش تطبیق شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectBack(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> ReverseReceiptApplication(
        int applicationId,
        string? reason = null,
        string? returnUrl = null)
    {
        try
        {
            await ReceiptApplications.ReverseApplicationAsync(applicationId, reason, User.Identity?.Name);
            TempData["ok"] = "تطبیق برگشت خورد و مانده دریافت آزاد شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectBack(returnUrl);
    }

    private IActionResult RedirectBack(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Index));

    /// <summary>دریافت‌های همان مشتری که هنوز مانده تطبیق‌نشده دارند.</summary>
    private async Task<List<SaleApplicableReceiptViewModel>> LoadApplicableReceiptsAsync(
        int customerId, int? excludeSaleId = null)
    {
        var candidates = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.CustomerId == customerId
                && p.PaymentKind == PaymentKind.CustomerReceipt
                && p.Direction == PaymentDirection.In
                && p.IsCustomerAdvance != true
                && p.AppliedFxRateToUsd != null
                && p.AppliedFxRateToUsd > 0m
                && (excludeSaleId == null || p.SalesTransactionId != excludeSaleId))
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.Amount,
                p.Currency,
                p.Reference,
                Rate = p.AppliedFxRateToUsd!.Value
            })
            .ToListAsync();

        var result = new List<SaleApplicableReceiptViewModel>();
        foreach (var payment in candidates)
        {
            var unapplied = await ReceiptApplications.GetUnappliedReceiptAmountAsync(payment.Id);
            if (unapplied <= 0.0001m)
            {
                continue;
            }

            result.Add(new SaleApplicableReceiptViewModel
            {
                PaymentTransactionId = payment.Id,
                PaymentDate = payment.PaymentDate,
                Amount = payment.Amount,
                UnappliedAmount = unapplied,
                UnappliedAmountUsd = decimal.Round(unapplied * payment.Rate, 4, MidpointRounding.AwayFromZero),
                Currency = payment.Currency,
                Reference = payment.Reference
            });
        }

        return result;
    }

    /// <summary>ردیف‌های تطبیقِ فعالِ مجموعه‌ای از فروش‌ها.</summary>
    private async Task<List<SaleReceiptApplicationViewModel>> LoadReceiptApplicationsAsync(int[] saleIds)
        => await _db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => saleIds.Contains(a.SalesTransactionId)
                && a.Status == CustomerPaymentAllocationApplicationStatus.Active)
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new SaleReceiptApplicationViewModel
            {
                ApplicationId = a.Id,
                PaymentTransactionId = a.PaymentTransactionId,
                SalesTransactionId = a.SalesTransactionId,
                InvoiceNumber = a.SalesTransaction!.InvoiceNumber ?? "",
                PaymentDate = a.PaymentTransaction!.PaymentDate,
                AppliedAt = a.AppliedAt,
                AppliedPaymentAmount = a.AppliedPaymentAmount,
                PaymentCurrencyCode = a.PaymentCurrencyCode,
                AppliedAmountUsd = a.AppliedAmountUsd,
                Reference = a.PaymentTransaction.Reference,
                IsAdvanceApplication = a.CustomerPaymentAllocationId != null
            })
            .ToListAsync();
}
