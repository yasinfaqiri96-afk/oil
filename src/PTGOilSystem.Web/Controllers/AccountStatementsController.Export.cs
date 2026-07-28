using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class AccountStatementsController
{
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] AccountStatementFilterViewModel? filter = null)
    {
        filter ??= new AccountStatementFilterViewModel();
        NormalizeFilter(filter);
        var statement = await BuildStatementRowsAsync(filter, page: 0);
        var items = statement.Items.ToList();

        var document = new TabularExportDocument
        {
            FileNameStem = "PTG_Account_Statements",
            TitleFa = "استیتمنت‌های ارزی",
            TitleEn = "Account Statements",
            KnownRowCount = items.Count,
            ForceLandscape = true,
            Filters =
            [
                new("از تاریخ", "From date", filter.FromDate?.ToString("yyyy-MM-dd")),
                new("تا تاریخ", "To date", filter.ToDate?.ToString("yyyy-MM-dd")),
                new("ارز منبع", "Source currency", filter.SourceCurrencyCode),
                new("مرجع", "Reference", filter.Reference),
                new("قرارداد", "Contract", filter.ContractId?.ToString()),
                new("مشتری", "Customer", filter.CustomerId?.ToString()),
                new("تأمین‌کننده", "Supplier", filter.SupplierId?.ToString())
            ],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13),
                new("سمت", "Side", Width: 12),
                new("مبلغ منبع", "Source amount", TabularExportValueType.Number, 16),
                new("ارز منبع", "Source currency", Width: 12),
                new("نرخ به USD", "FX to USD", TabularExportValueType.Number, 14),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new(CompanyFlowText.WithCurrency(CompanyFlowTextKey.RunningBalance, "USD", false),
                    CompanyFlowText.WithCurrency(CompanyFlowTextKey.RunningBalance, "USD", true),
                    TabularExportValueType.Number, 18),
                new("مرجع", "Reference", Width: 20, Wrap: true),
                new("منبع", "Source", Width: 18),
                new("قرارداد", "Contract", Width: 18),
                new("مشتری", "Customer", Width: 20),
                new("تأمین‌کننده", "Supplier", Width: 20),
                new("شرح", "Description", Width: 30, Wrap: true)
            ],
            Rows = items.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.EntryDate),
                TabularExportCell.Text(row.SideName),
                TabularExportCell.Number(row.SourceAmount),
                TabularExportCell.Text(row.SourceCurrencyCode),
                TabularExportCell.Number(row.AppliedFxRateToUsd),
                TabularExportCell.Number(row.AmountUsd),
                TabularExportCell.Number(row.RunningBalanceUsd),
                TabularExportCell.Text(row.Reference),
                TabularExportCell.Text($"{row.SourceType} #{row.SourceId}"),
                TabularExportCell.Text(row.ContractNumber),
                TabularExportCell.Text(row.CustomerName),
                TabularExportCell.Text(row.SupplierName),
                TabularExportCell.Text(row.Description)
            ]))
        };

        return TabularExportSupport.File(this, format, document);
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Csv([FromQuery] AccountStatementFilterViewModel? filter = null)
    {
        filter ??= new AccountStatementFilterViewModel();
        NormalizeFilter(filter);

        var statementRows = await BuildStatementRowsAsync(filter, page: 0);
        return CsvExportSupport.File(this, "account-statements.csv",
            ["Date", "Side", "SourceAmount", "SourceCurrency", "FxToUsd", "AmountUsd", "RunningBalanceUsd", "Reference", "SourceType", "SourceId", "Contract", "Customer", "Supplier", "Description"],
            statementRows.Items.Select(r => new[]
            {
                CsvExportSupport.Date(r.EntryDate),
                r.SideName,
                CsvExportSupport.Decimal(r.SourceAmount),
                r.SourceCurrencyCode,
                CsvExportSupport.Decimal(r.AppliedFxRateToUsd),
                CsvExportSupport.Decimal(r.AmountUsd),
                CsvExportSupport.Decimal(r.RunningBalanceUsd),
                r.Reference,
                r.SourceType,
                r.SourceId.ToString(),
                r.ContractNumber,
                r.CustomerName,
                r.SupplierName,
                r.Description
            }));
    }
}
