using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// قرارداد خرید چندتأمین‌کننده. هر ردیف یک <see cref="Contract"/> عادی است، پس بارگیری، رسید،
/// موجودی، پرداخت و دفتر کل همان مسیرهای قرارداد تک‌تأمین‌کننده را طی می‌کنند و هیچ اثری به حساب
/// تأمین‌کنندهٔ دیگر نمی‌رسد. ثبت قرارداد مثل قبل هیچ سند مالی یا موجودی نمی‌سازد.
/// </summary>
public partial class ContractsController
{
    private const string CreateGroupTokenPurpose = "Contract.CreateMultiSupplier";
    private const string AddGroupLineTokenPurpose = "Contract.AddPurchaseGroupLine";
    private const int MinimumPurchaseLines = 2;

    private static readonly string[] SingleContractOnlyFields =
    [
        nameof(ContractFormViewModel.ContractNumber),
        nameof(ContractFormViewModel.ProductId),
        nameof(ContractFormViewModel.UnitId),
        nameof(ContractFormViewModel.SupplierId),
        nameof(ContractFormViewModel.QuantityMt),
        nameof(ContractFormViewModel.UnitPriceInCurrency)
    ];

    private sealed record ValidPurchaseLine(int SupplierId, string SupplierName, int ProductId, int UnitId, decimal QuantityMt, decimal UnitPrice);

    private async Task<IActionResult> CreateMultiSupplierAsync(ContractFormViewModel model, string? formToken)
    {
        model.ContractType = ContractType.Purchase;
        model.PricingMethod = PricingMethod.Fixed;
        model.UiPricingType = UiPricingType.Agreed;
        model.SupplierId = null;
        NormalizeFormModel(model);
        await ResolveCompanyOwnerPartnerAsync(model);
        foreach (var field in SingleContractOnlyFields)
        {
            ModelState.Remove(field);
        }

        if (SimplePurchaseEnabled)
        {
            ModelState.AddModelError(string.Empty, "در Profile خرید ساده، قرارداد چندتأمین‌کننده پشتیبانی نمی‌شود.");
        }

        if (model.Status is not (ContractStatus.Draft or ContractStatus.Active))
        {
            ModelState.AddModelError(nameof(model.Status), "قرارداد جدید فقط می‌تواند پیش‌نویس یا فعال باشد.");
        }

        if (!await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == model.CompanyId && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CompanyId), "شرکت انتخاب‌شده معتبر نیست.");
        }

        await ValidateGroupCurrenciesAsync(model);
        await ValidateOwnershipAsync(model);
        ValidateRubSettlementModel(model);
        var lines = await ValidatePurchaseLinesAsync(model);

        var template = new Contract
        {
            ContractType = ContractType.Purchase,
            Status = model.Status,
            CompanyId = model.CompanyId,
            OwnershipType = model.OwnershipType,
            ContractDate = model.ContractDate,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            PricingMethod = PricingMethod.Fixed,
            Currency = model.Currency,
            SettlementCurrencyCode = model.SettlementCurrencyCode,
            RubRatePolicy = model.RubRatePolicy,
            ContractRubPerUsdRate = model.ContractRubPerUsdRate,
            ContractRubRateDate = model.ContractRubRateDate,
            ContractRubRateSource = model.ContractRubRateSource,
            AppliedFxRateToUsd = model.AppliedFxRateToUsd,
            Notes = model.Notes
        };
        ApplyContractCleanup(template);

        decimal rateToUsd = 1m;
        if (ModelState.IsValid)
        {
            try
            {
                var conversion = await _currencyConversion.ResolveToBaseAsync(
                    template.Currency,
                    template.ContractDate.Date,
                    template.AppliedFxRateToUsd);
                template.Currency = conversion.SourceCurrencyCode;
                template.AppliedFxRateToUsd = conversion.AppliedRateToBase;
                rateToUsd = conversion.AppliedRateToBase;
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            }
        }

        if (!ModelState.IsValid)
        {
            return await MultiSupplierFormViewAsync(model);
        }

        var groupNumber = await GenerateNextPurchaseGroupNumberAsync();
        var groupName = string.IsNullOrWhiteSpace(model.ContractName) ? groupNumber : model.ContractName;
        var partnerShares = GetNormalizedPartnerShares(model)
            .Select(share => (share.PartnerId!.Value, share.SharePercent!.Value))
            .ToList();
        var group = new PurchaseContractGroup
        {
            GroupNumber = groupNumber,
            Name = groupName,
            CompanyId = model.CompanyId,
            ContractDate = model.ContractDate,
            Notes = model.Notes
        };

        // قرارداد مادر شمارهٔ عادی خرید (P-xxx) می‌گیرد و هر تأمین‌کننده همان شماره با پسوند ردیف.
        for (var index = 0; index < lines.Count; index++)
        {
            group.Lines.Add(BuildPurchaseLine(template, lines[index], PurchaseLineNumber(groupNumber, index + 1), groupName, rateToUsd, partnerShares));
        }

        // سرِبرگ، همهٔ ردیف‌ها، شرکا و توکن در یک SaveChanges ذخیره می‌شوند: یا همه یا هیچ.
        _formTokens.Stamp(formToken, CreateGroupTokenPurpose, nameof(PurchaseContractGroup));
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        _db.PurchaseContractGroups.Add(group);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (_formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty, "شمارهٔ قرارداد هم‌زمان توسط کاربر دیگری استفاده شد؛ دوباره ذخیره کنید.");
            return await MultiSupplierFormViewAsync(model);
        }
        catch (PostgresException ex) when (IsPartnerShareViolation(ex))
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(model.PartnerShares), PartnerShareViolationMessage(ex));
            return await MultiSupplierFormViewAsync(model);
        }

        await AuditPurchaseGroupAsync(group, group.Lines);
        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        TempData["ok"] = $"قرارداد چندتأمین‌کننده با {group.Lines.Count} ردیف ثبت شد.";
        await SetBookOwnerWarningAsync(model.OwnershipType, model.CompanyId, partnerShares.Select(p => p.Item1));
        return RedirectToAction(nameof(PurchaseGroup), new { id = group.Id });
    }

    public async Task<IActionResult> PurchaseGroup(
        int id,
        [FromServices] IPartyBalanceReadService partyBalances,
        CancellationToken cancellationToken)
    {
        var group = await _db.PurchaseContractGroups
            .AsNoTracking()
            .Include(g => g.Company)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return NotFound();
        }

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.PurchaseContractGroupId == id && !c.IsArchived)
            .Include(c => c.Supplier)
            .Include(c => c.Product)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var contractIds = contracts.Select(c => c.Id).ToList();

        var loaded = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => contractIds.Contains(l.ContractId) && !l.IsCancelled)
            .GroupBy(l => l.ContractId)
            .Select(g => new { ContractId = g.Key, Quantity = g.Sum(l => l.LoadedQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.Quantity, cancellationToken);
        var received = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled && r.LoadingRegister != null && contractIds.Contains(r.LoadingRegister.ContractId))
            .GroupBy(r => r.LoadingRegister!.ContractId)
            .Select(g => new { ContractId = g.Key, Quantity = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.Quantity, cancellationToken);
        var balances = await partyBalances.GetContractBalancesAsync(contractIds, ct: cancellationToken, resolveNames: false);

        var lines = contracts
            .Select(contract =>
            {
                var price = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
                var loadedQuantity = loaded.GetValueOrDefault(contract.Id);
                var supplierBalance = balances.TryGetValue(contract.Id, out var summary)
                    ? summary.Parties.FirstOrDefault(party =>
                        party.PartyType == PartyStatementPartyType.Supplier && party.PartyId == contract.SupplierId)
                    : null;
                return new PurchaseContractGroupLineViewModel
                {
                    ContractId = contract.Id,
                    ContractNumber = contract.ContractNumber,
                    ContractLabel = contract.DisplayLabel,
                    SupplierId = contract.SupplierId,
                    SupplierName = contract.Supplier?.Name ?? "-",
                    ProductName = contract.Product?.Name ?? "-",
                    QuantityMt = contract.QuantityMt,
                    UnitPriceUsd = price,
                    ValueUsd = price * contract.QuantityMt,
                    LoadedQuantityMt = loadedQuantity,
                    ReceivedQuantityMt = received.GetValueOrDefault(contract.Id),
                    RemainingQuantityMt = Math.Max(0m, contract.QuantityMt - loadedQuantity),
                    PaidUsd = supplierBalance?.TotalOutflowUsd ?? 0m,
                    SupplierBalanceUsd = supplierBalance?.ClosingBalanceUsd ?? 0m,
                    Status = contract.Status
                };
            })
            .ToList();

        // چرخهٔ قرارداد (مصارف، ضایعات، پرداخت، مفاد و ضرر) از همان خلاصهٔ رسمی صفحهٔ جزئیات
        // قرارداد عادی برای هر ردیف لغونشده؛ قرارداد مادر فقط جمع آن‌ها است.
        IReadOnlyList<PTGOilSystem.Web.Models.ContractJourney.PurchaseGroupLineLifecycle> lineLifecycles = [];
        if (HttpContext?.RequestServices is { } services)
        {
            var journey = ActivatorUtilities.CreateInstance<ContractJourneyController>(services);
            journey.ControllerContext = ControllerContext;
            lineLifecycles = await journey.BuildPurchaseGroupLifecycleAsync(contracts
                .Where(c => c.Status != ContractStatus.Cancelled)
                .Select(c => c.Id)
                .ToList());
        }

        await PopulateLookupsAsync();
        return View(new PurchaseContractGroupDetailsViewModel
        {
            Id = group.Id,
            GroupNumber = group.GroupNumber,
            Name = group.Name,
            CompanyName = group.Company?.Name ?? "-",
            ContractDate = group.ContractDate,
            Notes = group.Notes,
            Currency = contracts.FirstOrDefault()?.Currency ?? SystemCurrency.BaseCurrencyCode,
            Lines = lines,
            // ردیف لغوشده تعهدی ندارد و در جمع قرارداد نمی‌آید؛ در جدول با وضعیت خودش دیده می‌شود.
            Totals = PurchaseGroupTotals.Calculate(lines
                .Where(line => line.Status != ContractStatus.Cancelled)
                .Select(line => new PurchaseGroupTotalsInput(line.SupplierId, line.QuantityMt, line.UnitPriceUsd))),
            CanAddLine = lines.Any(line => line.Status is ContractStatus.Draft or ContractStatus.Active),
            LineLifecycles = lineLifecycles,
            Lifecycle = lineLifecycles.Count > 0
                ? PTGOilSystem.Web.Models.ContractJourney.ContractLifecycleFigures.Sum(lineLifecycles.Select(l => l.Figures))
                : null
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPurchaseGroupLine(
        int groupId,
        PurchaseContractLineInput line,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var group = await _db.PurchaseContractGroups
            .Include(g => g.Lines)
            .ThenInclude(c => c.ContractPartners)
            .FirstOrDefaultAsync(g => g.Id == groupId);
        if (group is null)
        {
            return NotFound();
        }

        var redirect = RedirectToAction(nameof(PurchaseGroup), new { id = groupId });
        var template = group.Lines
            .Where(c => !c.IsArchived && c.Status is ContractStatus.Draft or ContractStatus.Active)
            .OrderBy(c => c.Id)
            .FirstOrDefault();
        if (template is null)
        {
            TempData["err"] = "این قرارداد ردیف فعالی ندارد و ردیف تازه نمی‌پذیرد.";
            return redirect;
        }

        var errors = await ValidateLineAsync(line);
        if (errors.Count > 0)
        {
            TempData["err"] = string.Join(" ", errors.Values);
            return redirect;
        }

        var supplierName = await _db.Suppliers.AsNoTracking()
            .Where(s => s.Id == line.SupplierId)
            .Select(s => s.Name)
            .SingleAsync();
        var unitId = await _db.Products.AsNoTracking()
            .Where(p => p.Id == line.ProductId)
            .Select(p => p.UnitId)
            .SingleAsync();

        decimal rateToUsd;
        try
        {
            var conversion = await _currencyConversion.ResolveToBaseAsync(
                template.Currency,
                template.ContractDate.Date,
                template.AppliedFxRateToUsd);
            rateToUsd = conversion.AppliedRateToBase;
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
            return redirect;
        }

        var partnerShares = template.ContractPartners
            .Where(partner => partner.EffectiveTo == null)
            .Select(partner => (partner.PartnerId, partner.SharePercent))
            .ToList();
        var contract = BuildPurchaseLine(
            template,
            new ValidPurchaseLine(line.SupplierId!.Value, supplierName, line.ProductId!.Value, unitId!.Value, line.QuantityMt!.Value, line.UnitPrice!.Value),
            await NextPurchaseLineNumberAsync(group),
            group.Name,
            rateToUsd,
            partnerShares);
        group.Lines.Add(contract);

        _formTokens.Stamp(formToken, AddGroupLineTokenPurpose, nameof(Contract));
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (_formTokens.IsDuplicate(ex))
        {
            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return redirect;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            TempData["err"] = "شمارهٔ قرارداد هم‌زمان توسط کاربر دیگری استفاده شد؛ دوباره ذخیره کنید.";
            return redirect;
        }

        await AuditPurchaseGroupAsync(null, [contract]);
        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        TempData["ok"] = "ردیف تازه به قرارداد اضافه شد.";
        return redirect;
    }

    private async Task<IActionResult> MultiSupplierFormViewAsync(ContractFormViewModel model)
    {
        EnsurePartnerRows(model);
        await PopulateLookupsAsync(model);
        return View("Create", model);
    }

    private async Task ValidateGroupCurrenciesAsync(ContractFormViewModel model)
    {
        var activeCurrencies = await _db.Currencies.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => c.Code)
            .ToListAsync();
        if (activeCurrencies.Count == 0)
        {
            return;
        }

        if (!activeCurrencies.Contains(model.Currency))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده در master data وجود ندارد یا غیرفعال است.");
        }

        if (model.RubRatePolicy != RubSettlementRatePolicy.NotApplicable
            && !SystemCurrency.IsBaseCurrency(model.SettlementCurrencyCode)
            && !string.Equals(model.SettlementCurrencyCode, "RUB", StringComparison.OrdinalIgnoreCase)
            && !activeCurrencies.Contains(model.SettlementCurrencyCode))
        {
            ModelState.AddModelError(
                nameof(model.SettlementCurrencyCode),
                "ارز تسویهٔ انتخاب‌شده در master data وجود ندارد یا غیرفعال است.");
        }
    }

    private async Task<List<ValidPurchaseLine>> ValidatePurchaseLinesAsync(ContractFormViewModel model)
    {
        var rows = model.PurchaseLines ?? [];
        var valid = new List<ValidPurchaseLine>();
        var filledCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row is null || row.IsEmpty)
            {
                continue;
            }

            filledCount++;
            var errors = await ValidateLineAsync(row);
            foreach (var error in errors)
            {
                ModelState.AddModelError($"{nameof(model.PurchaseLines)}[{index}].{error.Key}", error.Value);
            }

            if (errors.Count > 0)
            {
                continue;
            }

            var supplierName = await _db.Suppliers.AsNoTracking()
                .Where(s => s.Id == row.SupplierId)
                .Select(s => s.Name)
                .SingleAsync();
            var unitId = await _db.Products.AsNoTracking()
                .Where(p => p.Id == row.ProductId)
                .Select(p => p.UnitId)
                .SingleAsync();
            valid.Add(new ValidPurchaseLine(row.SupplierId!.Value, supplierName, row.ProductId!.Value, unitId!.Value, row.QuantityMt!.Value, row.UnitPrice!.Value));
        }

        if (filledCount < MinimumPurchaseLines)
        {
            ModelState.AddModelError(
                nameof(model.PurchaseLines),
                "قرارداد چندتأمین‌کننده حداقل دو ردیف خرید لازم دارد؛ برای یک ردیف، قرارداد عادی ثبت کنید.");
        }

        return valid;
    }

    private async Task<Dictionary<string, string>> ValidateLineAsync(PurchaseContractLineInput row)
    {
        var errors = new Dictionary<string, string>();
        if (!row.SupplierId.HasValue)
        {
            errors[nameof(row.SupplierId)] = "تأمین‌کننده را انتخاب کنید.";
        }
        else if (!await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == row.SupplierId.Value && s.IsActive))
        {
            errors[nameof(row.SupplierId)] = "تأمین‌کنندهٔ انتخاب‌شده معتبر نیست.";
        }

        if (!row.ProductId.HasValue)
        {
            errors[nameof(row.ProductId)] = "محصول را انتخاب کنید.";
        }
        else
        {
            var product = await _db.Products.AsNoTracking()
                .Where(p => p.Id == row.ProductId.Value && p.IsActive)
                .Select(p => new { p.UnitId })
                .FirstOrDefaultAsync();
            if (product is null)
            {
                errors[nameof(row.ProductId)] = "محصول انتخاب‌شده معتبر نیست.";
            }
            else if (!product.UnitId.HasValue)
            {
                errors[nameof(row.ProductId)] = "برای این محصول واحد تعریف نشده است.";
            }
        }

        if (row.QuantityMt is not > 0m)
        {
            errors[nameof(row.QuantityMt)] = "مقدار باید بزرگ‌تر از صفر باشد.";
        }

        if (row.UnitPrice is not > 0m)
        {
            errors[nameof(row.UnitPrice)] = "فی تن باید بزرگ‌تر از صفر باشد.";
        }

        return errors;
    }

    private static Contract BuildPurchaseLine(
        Contract template,
        ValidPurchaseLine line,
        string contractNumber,
        string groupName,
        decimal rateToUsd,
        IReadOnlyList<(int PartnerId, decimal SharePercent)> partnerShares)
    {
        var name = $"{groupName} — {line.SupplierName}";
        return new Contract
        {
            ContractName = name.Length > 200 ? name[..200] : name,
            ContractNumber = contractNumber,
            ContractType = ContractType.Purchase,
            Status = template.Status,
            CompanyId = template.CompanyId,
            ProductId = line.ProductId,
            UnitId = line.UnitId,
            SupplierId = line.SupplierId,
            DestinationLocationId = template.DestinationLocationId,
            PurchaseSourceLocationId = template.PurchaseSourceLocationId,
            OwnershipType = template.OwnershipType,
            ContractDate = template.ContractDate,
            StartDate = template.StartDate,
            EndDate = template.EndDate,
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = line.QuantityMt,
            Currency = template.Currency,
            UnitPriceInCurrency = line.UnitPrice,
            AppliedFxRateToUsd = rateToUsd,
            UnitPriceUsd = line.UnitPrice * rateToUsd,
            SettlementCurrencyCode = template.SettlementCurrencyCode,
            RubRatePolicy = template.RubRatePolicy,
            ContractRubPerUsdRate = template.ContractRubPerUsdRate,
            ContractRubRateDate = template.ContractRubRateDate,
            ContractRubRateSource = template.ContractRubRateSource,
            Notes = template.Notes,
            ContractPartners = partnerShares
                .Select(share => new ContractPartner
                {
                    PartnerId = share.PartnerId,
                    SharePercent = share.SharePercent,
                    EffectiveFrom = template.ContractDate.Date
                })
                .ToList()
        };
    }

    private async Task AuditPurchaseGroupAsync(PurchaseContractGroup? group, IEnumerable<Contract> lines)
    {
        if (group is not null)
        {
            await _audit.LogAndSaveAsync(
                nameof(PurchaseContractGroup),
                group.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("GroupNumber", group.GroupNumber),
                    ("Name", group.Name),
                    ("CompanyId", group.CompanyId),
                    ("ContractDate", group.ContractDate)));
        }

        foreach (var line in lines)
        {
            await _audit.LogAndSaveAsync(
                nameof(Contract),
                line.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ContractName", line.ContractName),
                    ("ContractNumber", line.ContractNumber),
                    ("ContractType", line.ContractType),
                    ("Status", line.Status),
                    ("PurchaseContractGroupId", line.PurchaseContractGroupId),
                    ("CompanyId", line.CompanyId),
                    ("ProductId", line.ProductId),
                    ("UnitId", line.UnitId),
                    ("SupplierId", line.SupplierId),
                    ("OwnershipType", line.OwnershipType),
                    ("Partners", BuildPartnerSummary(line.ContractPartners)),
                    ("PricingMethod", line.PricingMethod),
                    ("QuantityMt", line.QuantityMt),
                    ("Currency", line.Currency),
                    ("UnitPriceInCurrency", line.UnitPriceInCurrency),
                    ("AppliedFxRateToUsd", line.AppliedFxRateToUsd),
                    ("UnitPriceUsd", line.UnitPriceUsd),
                    ("ContractDate", line.ContractDate)));
        }
    }

    private async Task RemovePurchaseGroupIfLastLineAsync(Contract line)
    {
        if (line.PurchaseContractGroupId is not { } groupId
            || await _db.Contracts.AnyAsync(c => c.PurchaseContractGroupId == groupId && c.Id != line.Id))
        {
            return;
        }

        var group = await _db.PurchaseContractGroups.FirstOrDefaultAsync(g => g.Id == groupId);
        if (group is not null)
        {
            _db.PurchaseContractGroups.Remove(group);
        }
    }

    /// <summary>
    /// قرارداد مادر از همان دنبالهٔ شمارهٔ قرارداد خرید (P-xxx) شماره می‌گیرد تا کد آن هم‌شکل
    /// قراردادهای دیگر باشد. GenerateNextContractNumberAsync شمارهٔ قراردادهای مادر را هم می‌شمارد.
    /// </summary>
    private Task<string> GenerateNextPurchaseGroupNumberAsync()
        => GenerateNextContractNumberAsync(ContractType.Purchase);

    /// <summary>شمارهٔ ردیف تأمین‌کننده: شمارهٔ قرارداد مادر + پسوند ردیف، مثل P-011-1.</summary>
    internal static string PurchaseLineNumber(string groupNumber, int lineIndex) => $"{groupNumber}-{lineIndex}";

    private async Task<string> NextPurchaseLineNumberAsync(PurchaseContractGroup group)
    {
        var marker = group.GroupNumber + "-";
        var maxSuffix = group.Lines
            .Where(c => c.ContractNumber.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            .Select(c => int.TryParse(c.ContractNumber[marker.Length..], out var suffix) ? suffix : 0)
            .DefaultIfEmpty(0)
            .Max();
        var next = Math.Max(maxSuffix, group.Lines.Count);
        string candidate;
        do
        {
            next++;
            candidate = PurchaseLineNumber(group.GroupNumber, next);
        }
        while (await _db.Contracts.AsNoTracking().AnyAsync(c => c.ContractNumber == candidate));

        return candidate;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
