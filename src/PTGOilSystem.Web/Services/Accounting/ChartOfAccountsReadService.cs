using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;

namespace PTGOilSystem.Web.Services.Accounting;

public interface IChartOfAccountsReadService
{
    Task<ChartOfAccountsIndexViewModel> BuildAsync(
        string? search,
        int page,
        CancellationToken cancellationToken = default,
        int? pageSize = null);
}

/// <summary>
/// فهرست فقط‌خواندنیِ سرفصل حساب‌ها. همیشه فقط حساب‌های شرکتِ مالک را نشان می‌دهد؛ انتخابِ شرکت از
/// کاربر گرفته نمی‌شود. اگر هنوز مالکی تعیین نشده باشد، فهرست خالی با پیام مناسب برمی‌گردد.
/// </summary>
public sealed class ChartOfAccountsReadService(
    ApplicationDbContext db,
    ISystemCompanyProvider systemCompany) : IChartOfAccountsReadService
{
    private const int PageSize = 20;

    public async Task<ChartOfAccountsIndexViewModel> BuildAsync(
        string? search,
        int page,
        CancellationToken cancellationToken = default,
        int? pageSize = null)
    {
        var effectivePageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(pageSize, PageSize);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var ownerCompanyId = await systemCompany.FindOwnerCompanyIdAsync(cancellationToken);

        if (ownerCompanyId is null)
        {
            return new ChartOfAccountsIndexViewModel(
                OwnerCompanyId: null,
                normalizedSearch,
                [],
                CurrentPage: 1,
                PageCount: 1,
                TotalCount: 0,
                effectivePageSize);
        }

        var query = db.Accounts.AsNoTracking()
            .Where(account => account.CompanyId == ownerCompanyId.Value);

        if (normalizedSearch is not null)
        {
            query = query.Where(account =>
                account.Code.Contains(normalizedSearch)
                || account.Name.Contains(normalizedSearch)
                || (account.ParentAccount != null
                    && (account.ParentAccount.Code.Contains(normalizedSearch)
                        || account.ParentAccount.Name.Contains(normalizedSearch))));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)effectivePageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderBy(account => account.ParentAccountId.HasValue
                ? account.ParentAccount!.Code
                : account.Code)
            .ThenBy(account => account.ParentAccountId.HasValue)
            .ThenBy(account => account.Code)
            .Skip((currentPage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(account => new ChartOfAccountsRowViewModel(
                account.Id,
                account.CompanyId,
                account.Code,
                account.Name,
                account.AccountType,
                account.NormalBalance,
                account.IsActive,
                account.ParentAccountId,
                account.ParentAccount != null ? account.ParentAccount.Code : null,
                account.ParentAccount != null ? account.ParentAccount.Name : null))
            .ToListAsync(cancellationToken);

        return new ChartOfAccountsIndexViewModel(
            ownerCompanyId.Value,
            normalizedSearch,
            items,
            currentPage,
            pageCount,
            totalCount,
            effectivePageSize);
    }
}
