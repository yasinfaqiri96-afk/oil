using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ContractsController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, string? q, ContractType? type, ContractStatus? status, CancellationToken cancellationToken)
    {
        var query = _db.Contracts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c => c.ContractName.Contains(term)
                || c.ContractNumber.Contains(term)
                || (c.Supplier != null && c.Supplier.Name.Contains(term))
                || (c.Customer != null && c.Customer.Name.Contains(term))
                || (c.Product != null && c.Product.Name.Contains(term))
                || c.ContractPartners.Any(cp => cp.Partner != null && cp.Partner.Name.Contains(term)));
        }
        if (type.HasValue) query = query.Where(c => c.ContractType == type.Value);
        if (status.HasValue) query = query.Where(c => c.Status == status.Value);

        var rows = await query
            .OrderByDescending(c => c.ContractDate).ThenByDescending(c => c.Id)
            .Select(c => new
            {
                c.ContractName,
                c.ContractNumber,
                Party = c.ContractType == ContractType.Purchase ? c.Supplier!.Name : c.Customer!.Name,
                Product = c.Product != null ? c.Product.Name : "",
                Unit = c.Unit != null ? (c.Unit.Symbol ?? c.Unit.Code ?? c.Unit.Name) : "MT",
                c.QuantityMt, c.ContractType, c.ContractDate, c.EndDate, c.Status
            })
            .ToListAsync(cancellationToken);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Contracts",
            TitleFa = "قراردادها",
            TitleEn = "Contracts",
            KnownRowCount = rows.Count,
            Filters = TabularExportSupport.FilterSummary(("جستجو / Search", q), ("نوع / Type", type), ("وضعیت / Status", status)),
            Columns =
            [
                new("نام قرارداد", "Contract name", Width: 24), new("شماره قرارداد", "Contract no.", Width: 17),
                new("طرف قرارداد", "Party", Width: 22),
                new("جنس", "Product", Width: 18), new("مقدار", "Quantity", TabularExportValueType.Number, 15),
                new("واحد", "Unit", Width: 10), new("نوع", "Type", Width: 12),
                new("تاریخ قرارداد", "Contract date", TabularExportValueType.Date, 14),
                new("تاریخ ختم", "End date", TabularExportValueType.Date, 14), new("وضعیت", "Status", Width: 13)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.ContractName), TabularExportCell.Text(r.ContractNumber),
                TabularExportCell.Text(r.Party), TabularExportCell.Text(r.Product),
                TabularExportCell.Number(r.QuantityMt), TabularExportCell.Text(r.Unit), TabularExportCell.Text(r.ContractType.ToString()),
                TabularExportCell.Date(r.ContractDate), TabularExportCell.Date(r.EndDate), TabularExportCell.Text(r.Status.ToString())
            ]))
        });
    }
}
