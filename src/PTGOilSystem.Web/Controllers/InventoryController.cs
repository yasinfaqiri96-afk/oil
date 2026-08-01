using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Inventory;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class InventoryController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IAuditService _audit;
    private readonly ILogger<InventoryController> _logger;
    private const int LookupLimit = 200;
    private const int IndexPageSize = 20;

    public InventoryController(
        ApplicationDbContext db,
        IStockService stock,
        IAuditService audit,
        ILogger<InventoryController> logger)
    {
        _db = db;
        _stock = stock;
        _audit = audit;
        _logger = logger;
    }

    private async Task PopulateLookupsAsync(
        InventoryMovementCreateViewModel? createModel = null,
        InventoryStockCardFilterViewModel? cardFilter = null)
    {
        var selectedContractId = createModel?.ContractId ?? cardFilter?.ContractId;

        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => selectedContractId.HasValue && c.Id == selectedContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Products = new SelectList(
            await _db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(),
            "Id",
            "Name",
            createModel?.ProductId ?? cardFilter?.ProductId);

        ViewBag.Contracts = new SelectList(
            contracts
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedContractId);

        ViewBag.Terminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(),
            "Id",
            "Name",
            createModel?.TerminalId ?? cardFilter?.TerminalId);

        ViewBag.StorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            createModel?.StorageTankId);
    }

    public async Task<IActionResult> Index(string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var query = _db.InventoryMovements.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var searchTerm = q.Trim().ToLower();
            query = query.Where(m =>
                (m.Product != null && m.Product.Name.ToLower().Contains(searchTerm)) ||
                (m.Terminal != null && m.Terminal.Name.ToLower().Contains(searchTerm)) ||
                (m.Contract != null && m.Contract.ContractNumber.ToLower().Contains(searchTerm)) ||
                (m.StorageTank != null && (
                    m.StorageTank.TankCode.ToLower().Contains(searchTerm)
                    || (m.StorageTank.DisplayName != null && m.StorageTank.DisplayName.ToLower().Contains(searchTerm)))) ||
                (m.ReferenceDocument != null && m.ReferenceDocument.ToLower().Contains(searchTerm)) ||
                (m.Notes != null && m.Notes.ToLower().Contains(searchTerm)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(m => m.MovementDate)
            .ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new InventoryMovementListItemViewModel
            {
                Id = m.Id,
                MovementDate = m.MovementDate,
                Direction = m.Direction,
                QuantityMt = m.QuantityMt,
                ProductName = m.Product != null ? m.Product.Name : "",
                TerminalName = m.Terminal != null ? m.Terminal.Name : "",
                ContractNumber = m.Contract != null ? m.Contract.ContractNumber : null,
                StorageTankCode = m.StorageTank == null
                    ? null
                    : m.StorageTank.DisplayName == null || m.StorageTank.DisplayName == ""
                        ? m.StorageTank.TankCode
                        : m.StorageTank.DisplayName,
                ReferenceDocument = m.ReferenceDocument,
                Notes = m.Notes
            })
            .ToListAsync();

        return View(new InventoryIndexViewModel
        {
            Query = q,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new InventoryMovementCreateViewModel
        {
            MovementDate = AfghanistanBusinessClock.SystemToday,
            Direction = MovementDirection.In
        };

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(InventoryMovementCreateViewModel model)
    {
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ProductId && p.IsActive);
        if (product is null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        var terminal = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.TerminalId && t.IsActive);
        if (terminal is null)
        {
            ModelState.AddModelError(nameof(model.TerminalId), "ترمینال انتخاب‌شده معتبر نیست.");
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ProductId != model.ProductId)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده با کالای انتخابی هم‌خوان نیست.");
            }
        }

        if (model.StorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.StorageTankId.Value);
            if (tank is null)
            {
                ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده معتبر نیست.");
            }
            else
            {
                if (tank.TerminalId != model.TerminalId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده به ترمینال انتخابی تعلق ندارد.");
                }

                if (tank.ProductId.HasValue && tank.ProductId != model.ProductId)
                {
                    ModelState.AddModelError(nameof(model.StorageTankId), "مخزن انتخاب‌شده برای این کالا تعریف نشده است.");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var movement = new InventoryMovement
        {
            ProductId = model.ProductId,
            ContractId = model.ContractId,
            TerminalId = model.TerminalId,
            StorageTankId = model.StorageTankId,
            Direction = model.Direction,
            QuantityMt = model.QuantityMt,
            MovementDate = model.MovementDate,
            ReferenceDocument = string.IsNullOrWhiteSpace(model.ReferenceDocument) ? null : model.ReferenceDocument.Trim(),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };

        IDbContextTransaction? transaction = null;
        try
        {
            // Serialize the availability check with the write so two concurrent
            // Out-movements on the same tank cannot both pass and oversell.
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync();
            }

            await _stock.AcquireStockMutationLockAsync(movement);
            await _stock.EnsureSufficientStockForMovementAsync(movement);

            _db.InventoryMovements.Add(movement);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(InventoryMovement),
                movement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", movement.ProductId),
                    ("ContractId", movement.ContractId),
                    ("TerminalId", movement.TerminalId),
                    ("StorageTankId", movement.StorageTankId),
                    ("Direction", movement.Direction),
                    ("QuantityMt", movement.QuantityMt),
                    ("MovementDate", movement.MovementDate),
                    ("ReferenceDocument", movement.ReferenceDocument)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = "حرکت موجودی با موفقیت ثبت شد.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create inventory movement.");
            ModelState.AddModelError(string.Empty, "ثبت حرکت موجودی انجام نشد. دوباره تلاش کنید.");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    public async Task<IActionResult> StockSummary(int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var rows = (await _stock.GetStockSummaryAsync())
            .Select(r => new InventoryStockSummaryRowViewModel
            {
                ProductId = r.ProductId,
                ProductCode = r.ProductCode,
                ProductName = r.ProductName,
                TerminalId = r.TerminalId,
                TerminalCode = r.TerminalCode,
                TerminalName = r.TerminalName,
                ContractId = r.ContractId,
                ContractNumber = r.ContractNumber,
                FreeQuantityMt = r.FreeQuantityMt,
                LastMovementDate = r.LastMovementDate,
                MovementCount = r.MovementCount
            })
            .ToList();

        var totalCount = rows.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        return View(new InventoryStockSummaryIndexViewModel
        {
            Rows = rows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList(),
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> StockCard([Bind(Prefix = "Filter")] InventoryStockCardFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        filter ??= new InventoryStockCardFilterViewModel();

        if (filter.FromDate.HasValue && filter.ToDate.HasValue && filter.FromDate > filter.ToDate)
        {
            ModelState.AddModelError(string.Empty, "بازه تاریخ معتبر نیست.");
        }

        await PopulateLookupsAsync(cardFilter: filter);

        if (!ModelState.IsValid)
        {
            return View(new InventoryStockCardViewModel
            {
                Filter = filter
            });
        }

        var stockRows = await _stock.GetStockCardAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            fromUtc: filter.FromDate,
            toUtc: filter.ToDate);
        var tankIds = stockRows
            .Where(r => r.StorageTankId.HasValue)
            .Select(r => r.StorageTankId!.Value)
            .Distinct()
            .ToList();
        var tankNames = await StorageTankDisplay.LoadNamesAsync(
            _db.StorageTanks.AsNoTracking().Where(t => tankIds.Contains(t.Id)));

        var rows = stockRows
            .Select(r => new InventoryStockCardRowViewModel
            {
                MovementId = r.MovementId,
                MovementDate = r.MovementDate,
                Direction = r.Direction,
                QuantityMt = r.QuantityMt,
                SignedQuantityMt = r.SignedQuantityMt,
                RunningBalanceMt = r.RunningBalanceMt,
                ProductCode = r.ProductCode,
                ProductName = r.ProductName,
                TerminalCode = r.TerminalCode,
                TerminalName = r.TerminalName,
                ContractNumber = r.ContractNumber,
                StorageTankCode = StorageTankDisplay.Resolve(tankNames, r.StorageTankId, r.StorageTankCode),
                ReferenceDocument = r.ReferenceDocument,
                Notes = r.Notes
            })
            .ToList();

        var totalCount = rows.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        return View(new InventoryStockCardViewModel
        {
            Filter = filter,
            Rows = rows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList(),
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }
}
