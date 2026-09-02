using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.ViewComponents;

/// <summary>
/// تب «صورت‌حساب» پروفایل طرف‌حساب. پیش‌تر همین تب صفحهٔ مستقل صورت‌حساب را با fetch
/// می‌گرفت؛ یعنی یک رفت‌وبرگشت اضافه و محاسبهٔ دوبارهٔ کل صورت‌حساب. حالا همان
/// ViewModel سمت سرور ساخته و همان partial رندر می‌شود.
///
/// فقط خواندن است: همهٔ اعداد از PartyStatementPageBuilder می‌آیند و اینجا هیچ مبلغی
/// محاسبه نمی‌شود.
/// </summary>
public sealed class PartyStatementViewComponent : ViewComponent
{
    private readonly PartyStatementPageBuilder _pageBuilder;

    public PartyStatementViewComponent(PartyStatementPageBuilder pageBuilder)
    {
        _pageBuilder = pageBuilder;
    }

    public async Task<IViewComponentResult> InvokeAsync(
        PartyStatementPartyType partyType,
        int partyId,
        string? currencyCode = null,
        SupplierStatementView? view = null)
    {
        var effectiveView = PartyStatementPageBuilder.ResolveView(partyType, view);
        var filter = new PartyStatementFilter
        {
            CurrencyCode = currencyCode,
            IncludeOperationalColumns = PartyStatementPageBuilder.NeedsOperationalColumns(effectiveView)
        };

        var model = await _pageBuilder.BuildDocumentAsync(
            partyType,
            partyId,
            filter,
            print: false,
            effectiveView,
            HttpContext.RequestAborted);

        return View(new PartyStatementViewModel
        {
            Statement = model.Statement,
            Filter = model.Filter,
            IsPrintMode = false,
            IsEmbedded = true,
            SupplierView = model.SupplierView,
            HasContractRows = model.HasContractRows,
            ContractGrouping = model.ContractGrouping,
            ContractOptions = model.ContractOptions,
            CompanyOptions = model.CompanyOptions,
            CurrencyOptions = model.CurrencyOptions,
            PartyOptions = model.PartyOptions
        });
    }
}
