using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Ledger;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Sarrafs;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class LedgerController : Controller
{
    private const int IndexPageSize = 20;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LedgerController> _logger;
    private readonly IMemoryCache? _summaryCache;

    public LedgerController(ApplicationDbContext db, ILogger<LedgerController> logger, IMemoryCache? summaryCache = null)
    {
        _db = db;
        _logger = logger;
        _summaryCache = summaryCache;
    }

    public async Task<IActionResult> Index([FromQuery] LedgerIndexFilterViewModel? filter = null, int page = 1)
    {
        filter ??= new LedgerIndexFilterViewModel();

        await PopulateLookupsAsync(filter);

        // Projected to LedgerListItemViewModel below, so EF ignores any Include
        // on these navs — kept lean: the Select emits the needed LEFT JOINs.
        var query = ApplyLedgerFilter(_db.LedgerEntries
            .AsNoTracking(), filter);

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)IndexPageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .Skip((page - 1) * IndexPageSize)
            .Take(IndexPageSize)
            .Select(l => new LedgerListItemViewModel
            {
                Id = l.Id,
                EntryDate = l.EntryDate,
                Side = l.Side,
                SideName = GetSideName(l.Side),
                AmountUsd = l.AmountUsd,
                Currency = l.Currency,
                Description = l.Description,
                SourceType = l.SourceType,
                SourceTypeLabel = GetSourceTypeLabel(l.SourceType),
                SourceId = l.SourceId,
                Reference = l.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(l.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(l.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(l.SourceType) ? l.SourceId : null,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                CustomerName = l.Customer != null ? l.Customer.Name : null,
                SupplierName = l.Supplier != null ? l.Supplier.Name : null,
                EmployeeName = l.Employee != null ? l.Employee.FullName : null,
                ShipmentCode = l.Shipment != null ? l.Shipment.ShipmentCode : null
            })
            .ToListAsync();

        return View(new LedgerIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            FinanceMetrics = await FinanceMetricCardsQuery.BuildAsync(_db, _summaryCache)
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Csv([FromQuery] LedgerIndexFilterViewModel? filter = null)
    {
        filter ??= new LedgerIndexFilterViewModel();

        // بازهٔ تاریخ الزامی است تا خروجی کل جدول دفتر کل گرفته نشود.
        if (!filter.FromDate.HasValue || !filter.ToDate.HasValue)
        {
            TempData["error"] = "برای خروجی CSV، تعیین «از تاریخ» و «تا تاریخ» الزامی است.";
            return RedirectToAction(nameof(Index), filter);
        }

        // شمارش پیش از بارگذاری: اگر از سقف بیشتر بود، هیچ ردیفی به حافظه نمی‌آید.
        var totalCount = await ApplyLedgerFilter(_db.LedgerEntries.AsNoTracking(), filter).CountAsync();
        if (totalCount > CsvExportSupport.MaxRows)
        {
            TempData["error"] =
                $"این خروجی {totalCount:N0} ردیف دارد و از سقف مجاز {CsvExportSupport.MaxRows:N0} ردیف بیشتر است. " +
                "بازهٔ تاریخ را کوتاه‌تر کنید یا فیلتر بیشتری اعمال کنید.";
            return RedirectToAction(nameof(Index), filter);
        }

        var rows = await BuildLedgerRowsAsync(filter);
        return CsvExportSupport.File(this, "ledger.csv",
            ["Date", "Side", "AmountUsd", "Currency", "Description", "SourceType", "SourceId", "Reference", "Contract", "Customer", "Supplier", "Employee", "Shipment"],
            rows.Select(r => new[]
            {
                CsvExportSupport.Date(r.EntryDate), r.SideName, CsvExportSupport.Decimal(r.AmountUsd), r.Currency, r.Description,
                r.SourceType, r.SourceId.ToString(), r.Reference, r.ContractNumber, r.CustomerName, r.SupplierName, r.EmployeeName, r.ShipmentCode
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] LedgerIndexFilterViewModel? filter = null)
    {
        filter ??= new LedgerIndexFilterViewModel();
        if (!filter.FromDate.HasValue || !filter.ToDate.HasValue)
        {
            TempData["error"] = UiText.T(HttpContext,
                "برای خروجی، تعیین «از تاریخ» و «تا تاریخ» الزامی است.",
                "From date and to date are required for export.");
            return RedirectToAction(nameof(Index), filter);
        }

        var exportFormat = TabularExportSupport.ParseFormat(format);
        var exportService = HttpContext.RequestServices.GetRequiredService<ITabularExportService>();
        var query = ApplyLedgerFilter(_db.LedgerEntries.AsNoTracking(), filter);
        var totalCount = await query.CountAsync(HttpContext.RequestAborted);
        var limit = exportService.GetRowLimit(exportFormat);
        if (totalCount > limit)
        {
            TempData["error"] = UiText.T(HttpContext,
                $"تعداد اطلاعات زیاد است ({totalCount:N0})؛ بازه تاریخ یا فیلترها را محدود کنید. سقف مجاز: {limit:N0}.",
                $"There are too many records ({totalCount:N0}). Narrow the date range or filters. Maximum: {limit:N0}.");
            return RedirectToAction(nameof(Index), filter);
        }

        var rows = await BuildLedgerRowsAsync(filter);
        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Ledger",
            TitleFa = "دفتر کل",
            TitleEn = "General Ledger",
            KnownRowCount = totalCount,
            ForceLandscape = true,
            Filters =
            [
                new("از تاریخ", "From date", filter.FromDate?.ToString("yyyy-MM-dd")),
                new("تا تاریخ", "To date", filter.ToDate?.ToString("yyyy-MM-dd")),
                new("نوع منبع", "Source type", filter.SourceType),
                new("مرجع", "Reference", filter.Reference),
                new("قرارداد", "Contract", filter.ContractId?.ToString()),
                new("مشتری", "Customer", filter.CustomerId?.ToString()),
                new("تأمین‌کننده", "Supplier", filter.SupplierId?.ToString())
            ],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13), new("سمت", "Side", Width: 11),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16), new("ارز", "Currency", Width: 10),
                new("شرح", "Description", Width: 30, Wrap: true), new("نوع منبع", "Source type", Width: 18),
                new("شناسه منبع", "Source ID", TabularExportValueType.Integer, 12), new("مرجع", "Reference", Width: 18),
                new("قرارداد", "Contract", Width: 16), new("مشتری", "Customer", Width: 18),
                new("تأمین‌کننده", "Supplier", Width: 18), new("کارمند", "Employee", Width: 17), new("محموله", "Shipment", Width: 16)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.EntryDate), TabularExportCell.Text(row.SideName), TabularExportCell.Number(row.AmountUsd),
                TabularExportCell.Text(row.Currency), TabularExportCell.Text(row.Description), TabularExportCell.Text(row.SourceType),
                TabularExportCell.Integer(row.SourceId), TabularExportCell.Text(row.Reference), TabularExportCell.Text(row.ContractNumber),
                TabularExportCell.Text(row.CustomerName), TabularExportCell.Text(row.SupplierName), TabularExportCell.Text(row.EmployeeName),
                TabularExportCell.Text(row.ShipmentCode)
            ]))
        });
    }

    private static IQueryable<LedgerEntry> ApplyLedgerFilter(
        IQueryable<LedgerEntry> query,
        LedgerIndexFilterViewModel filter)
    {
        if (filter.FromDate.HasValue) query = query.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) query = query.Where(l => l.EntryDate <= filter.ToDate.Value);
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            var sourceType = filter.SourceType.Trim();
            query = query.Where(l => l.SourceType == sourceType);
        }
        if (filter.ContractId.HasValue) query = query.Where(l => l.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(l => l.CustomerId == filter.CustomerId.Value);
        if (filter.SupplierId.HasValue) query = query.Where(l => l.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var reference = filter.Reference.Trim();
            query = query.Where(l => l.Reference != null && l.Reference.Contains(reference));
        }
        if (filter.Side.HasValue) query = query.Where(l => l.Side == filter.Side.Value);

        return query;
    }

    private async Task<List<LedgerListItemViewModel>> BuildLedgerRowsAsync(LedgerIndexFilterViewModel filter)
    {
        // Projected to LedgerListItemViewModel below, so EF ignores any Include
        // on these navs — kept lean: the Select emits the needed LEFT JOINs.
        var query = ApplyLedgerFilter(_db.LedgerEntries
            .AsNoTracking(), filter);

        return await query
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new LedgerListItemViewModel
            {
                Id = l.Id,
                EntryDate = l.EntryDate,
                Side = l.Side,
                SideName = GetSideName(l.Side),
                AmountUsd = l.AmountUsd,
                Currency = l.Currency,
                Description = l.Description,
                SourceType = l.SourceType,
                SourceTypeLabel = GetSourceTypeLabel(l.SourceType),
                SourceId = l.SourceId,
                Reference = l.Reference,
                SourceDetailsController = IsThreeWaySettlementSource(l.SourceType) ? "ThreeWaySettlement" : null,
                SourceDetailsAction = IsThreeWaySettlementSource(l.SourceType) ? "Details" : null,
                SourceDetailsRouteId = IsThreeWaySettlementSource(l.SourceType) ? l.SourceId : null,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                CustomerName = l.Customer != null ? l.Customer.Name : null,
                SupplierName = l.Supplier != null ? l.Supplier.Name : null,
                EmployeeName = l.Employee != null ? l.Employee.FullName : null,
                ShipmentCode = l.Shipment != null ? l.Shipment.ShipmentCode : null
            })
            .ToListAsync();
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var entry = await _db.LedgerEntries
            .Include(l => l.Contract)
            .Include(l => l.Customer)
            .Include(l => l.Supplier)
            .Include(l => l.Employee)
            .Include(l => l.Shipment)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id);

        if (entry is null)
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true
            ? returnUrl
            : null;

        LedgerSourceTraceViewModel? sourceTrace = null;

        try
        {
            sourceTrace = await BuildSourceTraceAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load source trace for ledger entry {LedgerEntryId}.", entry.Id);
        }

        return View(new LedgerDetailsViewModel
        {
            Id = entry.Id,
            EntryDate = entry.EntryDate,
            Side = entry.Side,
            SideName = GetSideName(entry.Side),
            AmountUsd = entry.AmountUsd,
            Currency = entry.Currency,
            Description = entry.Description,
            SourceType = entry.SourceType,
            SourceTypeLabel = GetSourceTypeLabel(entry.SourceType),
            SourceId = entry.SourceId,
            Reference = entry.Reference,
            Contract = entry.Contract is null ? null : new LedgerRelationViewModel
            {
                Id = entry.Contract.Id,
                Label = entry.Contract.ContractNumber
            },
            Customer = entry.Customer is null ? null : new LedgerRelationViewModel
            {
                Id = entry.Customer.Id,
                Label = entry.Customer.Name
            },
            Supplier = entry.Supplier is null ? null : new LedgerRelationViewModel
            {
                Id = entry.Supplier.Id,
                Label = entry.Supplier.Name
            },
            Employee = entry.Employee is null ? null : new LedgerRelationViewModel
            {
                Id = entry.Employee.Id,
                Label = $"{entry.Employee.EmployeeCode} - {entry.Employee.FullName}"
            },
            Shipment = entry.Shipment is null ? null : new LedgerRelationViewModel
            {
                Id = entry.Shipment.Id,
                Label = entry.Shipment.ShipmentCode
            },
            SourceTrace = sourceTrace,
            IsViaSarrafSupplierPayment = entry.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType,
            HasViaSarrafGroup = entry.ViaSarrafGroupId.HasValue
        });
    }

    private async Task PopulateLookupsAsync(LedgerIndexFilterViewModel filter)
    {
        var contractIds = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId.HasValue)
            .Select(l => l.ContractId!.Value)
            .Distinct()
            .ToListAsync();
        ViewBag.Contracts = new SelectList(
            ContractUiText.ToLookupOptions(
                await _db.Contracts
                    .AsNoTracking()
                    .Include(c => c.Product)
                    .Include(c => c.Unit)
                    .Where(c => contractIds.Contains(c.Id))
                    .OrderByDescending(c => c.ContractDate)
                    .ThenBy(c => c.ContractNumber)
                    .ToListAsync()),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            filter.ContractId);

        var customerIds = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.CustomerId.HasValue)
            .Select(l => l.CustomerId!.Value)
            .Distinct()
            .ToListAsync();
        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .OrderBy(c => c.Name)
                .ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId);

        var supplierIds = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SupplierId.HasValue)
            .Select(l => l.SupplierId!.Value)
            .Distinct()
            .ToListAsync();
        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .OrderBy(s => s.Name)
                .ToListAsync(),
            "Id",
            "Name",
            filter.SupplierId);

        var sourceTypes = await _db.LedgerEntries
            .AsNoTracking()
            .Select(l => l.SourceType)
            .Where(s => s != "")
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();
        ViewBag.SourceTypes = sourceTypes
            .Select(s => new SelectListItem
            {
                Value = s,
                Text = s,
                Selected = string.Equals(filter.SourceType, s, StringComparison.Ordinal)
            })
            .ToList();

        ViewBag.Sides = Enum.GetValues<LedgerSide>()
            .Select(side => new SelectListItem
            {
                Value = ((int)side).ToString(),
                Text = GetSideName(side),
                Selected = filter.Side == side
            })
            .ToList();
    }

    private async Task<LedgerSourceTraceViewModel?> BuildSourceTraceAsync(LedgerEntry entry)
    {
        if (string.Equals(entry.SourceType, "Sale", StringComparison.OrdinalIgnoreCase))
        {
            var sale = await _db.SalesTransactions
                .Include(s => s.Contract)
                .Include(s => s.Customer)
                .Include(s => s.Shipment)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == entry.SourceId);

            if (sale is null)
            {
                return null;
            }

            var sourceMovement = await _db.InventoryMovements
                .Include(m => m.Contract)
                .Include(m => m.Terminal)
                .Include(m => m.StorageTank)
                .AsNoTracking()
                .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync();

            var fields = new List<LedgerTraceFieldViewModel>
            {
                new() { Label = "فاکتور / مرجع", Value = sale.InvoiceNumber },
                new() { Label = "تاریخ فروش", Value = DateDisplay.Date(sale.SaleDate) },
                new() { Label = "مرحله فروش", Value = SaleStageLabels.ToPersian(sale.SaleStage) },
                new() { Label = "مبلغ فروش (USD)", Value = sale.TotalUsd.ToString("N2") }
            };

            if (sale.Contract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد فروش", Value = sale.Contract.ContractNumber });
            }

            if (sale.Customer is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "مشتری", Value = sale.Customer.Name });
            }

            if (sale.Shipment is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "Shipment", Value = sale.Shipment.ShipmentCode });
            }

            if (sourceMovement?.Contract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد خرید منبع موجودی", Value = sourceMovement.Contract.ContractNumber });
            }

            if (sourceMovement?.Terminal is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "ترمینال مبدا", Value = sourceMovement.Terminal.Name });
            }

            if (sourceMovement?.StorageTank is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "مخزن مبدا", Value = StorageTankDisplay.Build(sourceMovement.StorageTank) });
            }

            if (!string.IsNullOrWhiteSpace(sale.Notes))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "یادداشت", Value = sale.Notes! });
            }

            return new LedgerSourceTraceViewModel
            {
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Title = "فروش",
                ControllerName = "Sales",
                Fields = fields
            };
        }

        if (string.Equals(entry.SourceType, "Expense", StringComparison.OrdinalIgnoreCase))
        {
            var expense = await _db.ExpenseTransactions
                .Include(e => e.ExpenseType)
                .Include(e => e.Contract)
                .Include(e => e.Shipment)
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == entry.SourceId);

            if (expense is null)
            {
                return null;
            }

            var fields = new List<LedgerTraceFieldViewModel>
            {
                new() { Label = "تاریخ هزینه", Value = DateDisplay.Date(expense.ExpenseDate) },
                new() { Label = "مبلغ هزینه (USD)", Value = expense.AmountUsd.ToString("N2") }
            };

            if (expense.ExpenseType is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel
                {
                    Label = "نوع مصرف",
                    Value = expense.ExpenseType.NamePersian ?? expense.ExpenseType.Name
                });
            }

            if (expense.Contract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد", Value = expense.Contract.ContractNumber });
            }

            if (expense.Shipment is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "Shipment", Value = expense.Shipment.ShipmentCode });
            }

            if (!string.IsNullOrWhiteSpace(expense.Description))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "شرح / مرجع", Value = expense.Description! });
            }

            return new LedgerSourceTraceViewModel
            {
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Title = "هزینه",
                ControllerName = "Expenses",
                Fields = fields
            };
        }

        if (IsPaymentSourceType(entry.SourceType))
        {
            var payment = await _db.PaymentTransactions
                .Include(p => p.CashAccount)
                .Include(p => p.Customer)
                .Include(p => p.Supplier)
                .Include(p => p.Driver)
                .Include(p => p.Employee)
                .Include(p => p.Contract)
                .Include(p => p.Shipment)
                .Include(p => p.SalesTransaction)
                .Include(p => p.ExpenseTransaction)
                .Include(p => p.TruckDispatch)
                    .ThenInclude(d => d!.Truck)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == entry.SourceId);

            if (payment is null)
            {
                return null;
            }

            var fields = new List<LedgerTraceFieldViewModel>
            {
                new() { Label = "تاریخ", Value = DateDisplay.Date(payment.PaymentDate) },
                new() { Label = "جهت", Value = PaymentDirectionLabels.ToPersian(payment.Direction) },
                new() { Label = "نوع", Value = PaymentKindLabels.ToPersian(payment.PaymentKind) },
                new() { Label = "مبلغ منبع", Value = $"{payment.Amount:N2} {payment.Currency}" },
                new() { Label = "مبلغ (USD)", Value = payment.AmountUsd.ToString("N2") }
            };

            if (payment.CashAccount is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel
                {
                    Label = "حساب نقد / بانک",
                    Value = $"{payment.CashAccount.Code} - {payment.CashAccount.Name}"
                });
            }

            if (payment.Customer is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "مشتری", Value = payment.Customer.Name });
            }

            if (payment.Supplier is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "تأمین‌کننده", Value = payment.Supplier.Name });
            }

            if (payment.Driver is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "راننده", Value = payment.Driver.FullName });
            }

            if (payment.Employee is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "کارمند", Value = $"{payment.Employee.EmployeeCode} - {payment.Employee.FullName}" });
            }

            if (payment.Contract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد", Value = payment.Contract.ContractNumber });
            }

            if (payment.Shipment is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "Shipment", Value = payment.Shipment.ShipmentCode });
            }

            if (payment.SalesTransaction is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "فروش", Value = payment.SalesTransaction.InvoiceNumber });
            }

            if (payment.ExpenseTransaction is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "هزینه", Value = payment.ExpenseTransaction.Description ?? ("Expense #" + payment.ExpenseTransaction.Id) });
            }

            if (payment.TruckDispatch is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel
                {
                    Label = "Truck Dispatch",
                    Value = payment.TruckDispatch.Truck is null
                        ? "#" + payment.TruckDispatch.Id
                        : $"#{payment.TruckDispatch.Id} - {payment.TruckDispatch.Truck.PlateNumber}"
                });
            }

            if (payment.AppliedFxRateToUsd.HasValue)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "نرخ FX به USD", Value = payment.AppliedFxRateToUsd.Value.ToString("0.######") });
            }

            if (!string.IsNullOrWhiteSpace(payment.Reference))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "مرجع", Value = payment.Reference! });
            }

            if (!string.IsNullOrWhiteSpace(payment.Description))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "شرح", Value = payment.Description! });
            }

            return new LedgerSourceTraceViewModel
            {
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Title = "پرداخت / دریافت",
                ControllerName = "Payments",
                Fields = fields
            };
        }

        if (IsThreeWaySettlementSource(entry.SourceType))
        {
            var settlement = await _db.ThreeWaySettlements
                .Include(s => s.Customer)
                .Include(s => s.Supplier)
                .Include(s => s.CustomerSaleContract)
                .Include(s => s.SupplierPurchaseContract)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == entry.SourceId);

            if (settlement is null)
            {
                return null;
            }

            var fields = new List<LedgerTraceFieldViewModel>
            {
                new() { Label = "تاریخ حواله", Value = DateDisplay.Date(settlement.SettlementDate) },
                new() { Label = "وضعیت سند", Value = settlement.Status == ThreeWaySettlementStatus.Cancelled ? "لغو شده" : "ثبت شده" },
                new() { Label = "مشتری", Value = settlement.Customer?.Name ?? "-" },
                new() { Label = "تأمین‌کننده", Value = settlement.Supplier?.Name ?? "-" },
                new() { Label = "مبلغ پرداختی مشتری", Value = $"{settlement.CustomerPaidAmount:N2} {settlement.Currency} / {settlement.CustomerPaidUsd:N2} USD" },
                new() { Label = "مبلغ قبول‌شده تأمین‌کننده", Value = $"{settlement.SupplierAcceptedAmount:N2} {settlement.Currency} / {settlement.SupplierAcceptedUsd:N2} USD" },
                new() { Label = "تفاوت مبلغ", Value = $"{settlement.DifferenceUsd:N2} USD" },
                new() { Label = "صندوق / بانک شرکت", Value = "بدون تغییر - پول وارد صندوق شرکت نمی‌شود" }
            };

            if (settlement.DifferenceReason.HasValue)
            {
                fields.Add(new LedgerTraceFieldViewModel
                {
                    Label = "دلیل تفاوت",
                    Value = SarrafSettlementLabels.ToPersian(settlement.DifferenceReason)
                });
            }

            if (settlement.CustomerSaleContract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد فروش مشتری", Value = settlement.CustomerSaleContract.ContractNumber });
            }

            if (settlement.SupplierPurchaseContract is not null)
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "قرارداد خرید تأمین‌کننده", Value = settlement.SupplierPurchaseContract.ContractNumber });
            }

            if (!string.IsNullOrWhiteSpace(settlement.HawalaReference))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "شماره حواله / مرجع", Value = settlement.HawalaReference! });
            }

            if (string.Equals(entry.SourceType, ThreeWaySettlementController.CancellationLedgerSourceType, StringComparison.Ordinal))
            {
                fields.Add(new LedgerTraceFieldViewModel { Label = "نوع trace", Value = "برگشت تسویه سه‌طرفه" });
                fields.Add(new LedgerTraceFieldViewModel { Label = "دلیل لغو", Value = settlement.CancellationReason ?? "-" });
            }

            return new LedgerSourceTraceViewModel
            {
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Title = GetSourceTypeLabel(entry.SourceType),
                ControllerName = "ThreeWaySettlement",
                Fields = fields
            };
        }

        return null;
    }

    private static bool IsPaymentSourceType(string sourceType)
        => Enum.GetNames<PaymentKind>().Contains(sourceType);

    private static bool IsThreeWaySettlementSource(string sourceType)
        => string.Equals(sourceType, ThreeWaySettlementController.LedgerSourceType, StringComparison.Ordinal)
            || string.Equals(sourceType, ThreeWaySettlementController.CancellationLedgerSourceType, StringComparison.Ordinal);

    private static string GetSourceTypeLabel(string sourceType)
        => sourceType switch
        {
            ThreeWaySettlementController.LedgerSourceType => "تسویه سه‌طرفه / حواله",
            ThreeWaySettlementController.CancellationLedgerSourceType => "برگشت تسویه سه‌طرفه",
            _ => sourceType
        };

    private static string GetSideName(LedgerSide side)
        => side == LedgerSide.Debit ? "بدهکار" : "بستانکار";
}
