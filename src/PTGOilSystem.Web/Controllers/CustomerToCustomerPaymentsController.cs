using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Finance;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// پرداخت/دریافت بین مشتریان — فقط ثبت و گزارش. عمداً هیچ LedgerEntry، PaymentTransaction،
// حرکت صندوق/بانک یا اثر روی مانده و سود و زیان شرکت ساخته نمی‌شود.
[Authorize]
public class CustomerToCustomerPaymentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IAfghanistanBusinessClock _businessClock;

    public CustomerToCustomerPaymentsController(ApplicationDbContext db, IAuditService audit, IAfghanistanBusinessClock businessClock)
    {
        _db = db;
        _audit = audit;
        _businessClock = businessClock;
    }

    public async Task<IActionResult> Index(string? q, int? customerId, string? currency, DateTime? from, DateTime? to,
        int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.CustomerToCustomerPayments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            q = q.Trim();
            query = query.Where(p => (p.Reference != null && p.Reference.Contains(q))
                || (p.Description != null && p.Description.Contains(q))
                || p.PayerCustomer!.Name.Contains(q)
                || p.PayeeCustomer!.Name.Contains(q));
        }
        if (customerId.HasValue)
            query = query.Where(p => p.PayerCustomerId == customerId.Value || p.PayeeCustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(currency))
            query = query.Where(p => p.Currency == currency);
        if (from.HasValue)
            query = query.Where(p => p.PaymentDate >= from.Value.Date);
        if (to.HasValue)
            query = query.Where(p => p.PaymentDate < to.Value.Date.AddDays(1));

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["customerId"] = customerId;
        ViewData["currency"] = currency;
        ViewData["from"] = from?.ToString("yyyy-MM-dd");
        ViewData["to"] = to?.ToString("yyyy-MM-dd");
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;
        ViewData["TotalsByCurrency"] = await query
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Total = g.Sum(p => p.Amount) })
            .OrderBy(g => g.Currency)
            .ToDictionaryAsync(g => g.Currency, g => g.Total);
        ViewBag.Customers = await CustomerSelectListAsync(customerId);
        ViewBag.Currencies = await CurrencySelectListAsync(currency);

        return View(await query
            .Include(p => p.PayerCustomer)
            .Include(p => p.PayeeCustomer)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    // صورت‌حساب یک مشتری: هر ردیف یا دریافتی (مشتری دریافت‌کننده بوده) یا پرداختی است.
    // مانده جدا برای هر ارز = دریافتی − پرداختی؛ ماندهٔ اول دوره از ردیف‌های قبل از «از تاریخ» ساخته می‌شود.
    public async Task<IActionResult> Statement(int? customerId, int? counterpartyId, string? currency, DateTime? from, DateTime? to)
    {
        var model = new CustomerToCustomerStatementViewModel
        {
            CustomerId = customerId,
            CounterpartyId = counterpartyId,
            Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim(),
            From = from?.Date,
            To = to?.Date
        };
        ViewBag.Customers = await CustomerSelectListAsync(customerId);
        ViewBag.Counterparties = await CustomerSelectListAsync(counterpartyId);
        ViewBag.Currencies = await CurrencySelectListAsync(model.Currency);

        if (!customerId.HasValue) return View(model);

        model.CustomerName = await _db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId.Value)
            .Select(c => c.Name)
            .FirstOrDefaultAsync();
        if (model.CustomerName == null) return NotFound();

        var id = customerId.Value;
        var query = _db.CustomerToCustomerPayments.AsNoTracking()
            .Where(p => p.PayerCustomerId == id || p.PayeeCustomerId == id);
        if (counterpartyId.HasValue)
            query = query.Where(p => p.PayerCustomerId == counterpartyId.Value || p.PayeeCustomerId == counterpartyId.Value);
        if (model.Currency != null)
            query = query.Where(p => p.Currency == model.Currency);
        if (model.To.HasValue)
            query = query.Where(p => p.PaymentDate < model.To.Value.AddDays(1));

        var opening = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (model.From.HasValue)
        {
            var before = await query
                .Where(p => p.PaymentDate < model.From.Value)
                .GroupBy(p => p.Currency)
                .Select(g => new
                {
                    Currency = g.Key,
                    Net = g.Sum(p => p.PayeeCustomerId == id ? p.Amount : -p.Amount)
                })
                .ToListAsync();
            foreach (var b in before) opening[b.Currency] = b.Net;
            query = query.Where(p => p.PaymentDate >= model.From.Value);
        }

        var rows = await query
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.PayerCustomerId,
                PayerName = p.PayerCustomer!.Name,
                PayeeName = p.PayeeCustomer!.Name,
                p.Amount,
                p.Currency,
                p.Reference,
                p.Description
            })
            .ToListAsync();

        var running = new Dictionary<string, decimal>(opening, StringComparer.OrdinalIgnoreCase);
        var received = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var paid = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var isPaid = r.PayerCustomerId == id;
            var dict = isPaid ? paid : received;
            dict[r.Currency] = dict.GetValueOrDefault(r.Currency) + r.Amount;
            running[r.Currency] = running.GetValueOrDefault(r.Currency) + (isPaid ? -r.Amount : r.Amount);
            model.Rows.Add(new CustomerToCustomerStatementRow(
                r.Id, r.PaymentDate, isPaid ? r.PayeeName : r.PayerName,
                isPaid ? 0m : r.Amount, isPaid ? r.Amount : 0m,
                r.Currency, running[r.Currency], r.Reference, r.Description));
        }

        foreach (var cur in running.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            model.Totals.Add(new CustomerToCustomerStatementTotal(
                cur, opening.GetValueOrDefault(cur), received.GetValueOrDefault(cur),
                paid.GetValueOrDefault(cur), running[cur]));
        }

        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new CustomerToCustomerPayment { PaymentDate = _businessClock.Today };
        await PopulateLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("PaymentDate,PayerCustomerId,PayeeCustomerId,Amount,Currency,Reference,Description")] CustomerToCustomerPayment model,
        string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model);
            return View(model);
        }

        _db.CustomerToCustomerPayments.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(CustomerToCustomerPayment), model.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("PaymentDate", model.PaymentDate.ToString("yyyy-MM-dd")),
                ("PayerCustomerId", model.PayerCustomerId),
                ("PayeeCustomerId", model.PayeeCustomerId),
                ("Amount", model.Amount),
                ("Currency", model.Currency)));
        TempData["ok"] = "پرداخت بین مشتریان ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.CustomerToCustomerPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (item == null) return NotFound();
        await PopulateLookupsAsync(item);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id,
        [Bind("Id,PaymentDate,PayerCustomerId,PayeeCustomerId,Amount,Currency,Reference,Description")] CustomerToCustomerPayment model)
    {
        if (id != model.Id) return BadRequest();
        Normalize(model);
        await ValidateAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model);
            return View(model);
        }

        var existing = await _db.CustomerToCustomerPayments.FirstOrDefaultAsync(p => p.Id == id);
        if (existing == null) return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("PaymentDate", existing.PaymentDate.ToString("yyyy-MM-dd"), model.PaymentDate.ToString("yyyy-MM-dd")),
            ("PayerCustomerId", existing.PayerCustomerId, model.PayerCustomerId),
            ("PayeeCustomerId", existing.PayeeCustomerId, model.PayeeCustomerId),
            ("Amount", existing.Amount, model.Amount),
            ("Currency", existing.Currency, model.Currency));
        existing.PaymentDate = model.PaymentDate;
        existing.PayerCustomerId = model.PayerCustomerId;
        existing.PayeeCustomerId = model.PayeeCustomerId;
        existing.Amount = model.Amount;
        existing.Currency = model.Currency;
        existing.Reference = model.Reference;
        existing.Description = model.Description;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(CustomerToCustomerPayment), existing.Id, AuditAction.Update, diff: diff);
        TempData["ok"] = "ویرایش با موفقیت انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.CustomerToCustomerPayments.FirstOrDefaultAsync(p => p.Id == id);
        if (item == null) return NotFound();

        var diff = AuditDiffFormatter.ForDelete(
            ("PaymentDate", item.PaymentDate.ToString("yyyy-MM-dd")),
            ("PayerCustomerId", item.PayerCustomerId),
            ("PayeeCustomerId", item.PayeeCustomerId),
            ("Amount", item.Amount),
            ("Currency", item.Currency));
        _db.CustomerToCustomerPayments.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(CustomerToCustomerPayment), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "پرداخت بین مشتریان حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(CustomerToCustomerPayment model)
    {
        if (model.PayerCustomerId <= 0)
            ModelState.AddModelError(nameof(model.PayerCustomerId), "مشتری پرداخت‌کننده را انتخاب کنید.");
        if (model.PayeeCustomerId <= 0)
            ModelState.AddModelError(nameof(model.PayeeCustomerId), "مشتری دریافت‌کننده را انتخاب کنید.");
        if (model.PayerCustomerId > 0 && model.PayerCustomerId == model.PayeeCustomerId)
            ModelState.AddModelError(nameof(model.PayeeCustomerId), "پرداخت‌کننده و دریافت‌کننده نمی‌توانند یک مشتری باشند.");
        if (model.Amount <= 0)
            ModelState.AddModelError(nameof(model.Amount), "مبلغ باید بزرگ‌تر از صفر باشد.");
        if (model.PaymentDate == default)
            ModelState.AddModelError(nameof(model.PaymentDate), "تاریخ را وارد کنید.");

        var ids = new[] { model.PayerCustomerId, model.PayeeCustomerId }.Where(i => i > 0).Distinct().ToArray();
        if (ids.Length > 0 && await _db.Customers.CountAsync(c => ids.Contains(c.Id)) != ids.Length)
            ModelState.AddModelError(string.Empty, "مشتری انتخاب‌شده یافت نشد.");
        if (!await _db.Currencies.AnyAsync(c => c.Code == model.Currency))
            ModelState.AddModelError(nameof(model.Currency), "ارز نامعتبر است.");
    }

    private static void Normalize(CustomerToCustomerPayment model)
    {
        model.PaymentDate = model.PaymentDate.Date;
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? "USD" : model.Currency.Trim().ToUpperInvariant();
        model.Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
    }

    private async Task PopulateLookupsAsync(CustomerToCustomerPayment model)
    {
        ViewBag.PayerCustomers = await CustomerSelectListAsync(model.PayerCustomerId, model.PayeeCustomerId);
        ViewBag.PayeeCustomers = await CustomerSelectListAsync(model.PayeeCustomerId, model.PayerCustomerId);
        ViewBag.Currencies = await CurrencySelectListAsync(model.Currency);
    }

    private async Task<SelectList> CustomerSelectListAsync(int? selectedId, int? alsoIncludeId = null)
    {
        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.IsActive || c.Id == selectedId || c.Id == alsoIncludeId)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();
        return new SelectList(customers, "Id", "Name", selectedId);
    }

    private async Task<SelectList> CurrencySelectListAsync(string? selected)
        => new(await _db.Currencies.AsNoTracking()
                .Where(c => c.IsActive || c.Code == selected)
                .OrderBy(c => c.Code)
                .Select(c => c.Code)
                .ToListAsync(),
            selected);
}
