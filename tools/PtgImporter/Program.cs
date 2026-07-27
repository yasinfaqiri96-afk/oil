using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

// One-off, idempotent importer for the "Petrogas -2026.xlsx" four contracts.
// Uses the real ApplicationDbContext + real services (SarrafSettlementService,
// SupplierLoadingLedger). No schema/migration/financial-logic change.
// Modes:  dryrun (default, no writes)  |  write

var mode = (args.FirstOrDefault() ?? "dryrun").Trim().ToLowerInvariant();
var write = mode == "write";
var conn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("FATAL: ConnectionStrings__DefaultConnection not set.");
    return 2;
}

var loadingsPath = Path.Combine(AppContext.BaseDirectory, "loadings.json");
if (!File.Exists(loadingsPath))
{
    Console.Error.WriteLine($"FATAL: loadings.json not found at {loadingsPath}");
    return 2;
}

var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(conn)
    .Options;

Console.WriteLine($"=== PTG Petrogas importer :: mode={mode} ===");
var importer = new Importer(options, loadingsPath, write);
try
{
    await importer.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine("FATAL during import: " + ex);
    return 1;
}
return 0;

// ----------------------------------------------------------------------------

sealed class Importer
{
    const string ViaSarrafPaymentSource = "SupplierViaSarrafPayment";
    const string ViaSarrafPayableSource = "SupplierViaSarrafPayable";
    const string LoadingLedgerSource = "Loading";

    readonly DbContextOptions<ApplicationDbContext> _options;
    readonly string _loadingsPath;
    readonly bool _write;

    readonly List<string> _created = new();
    readonly List<string> _skipped = new();
    readonly List<string> _blocked = new();

    public Importer(DbContextOptions<ApplicationDbContext> options, string loadingsPath, bool write)
    {
        _options = options;
        _loadingsPath = loadingsPath;
        _write = write;
    }

    // App stores these date columns as timestamptz and normalizes Unspecified->Utc on save
    // (ApplicationDbContext.NormalizeDateTimePropertiesToUtc). Query parameters bypass that
    // normalization, so build dates as Utc-midnight directly to match stored values.
    static DateTime D(int y, int m, int d) => DateTime.SpecifyKind(new DateTime(y, m, d), DateTimeKind.Utc);
    static DateTime DParse(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    public async Task RunAsync()
    {
        await using var db = new ApplicationDbContext(_options);
        Console.WriteLine("DB: " + db.Database.GetDbConnection().Database + " @ " + db.Database.GetDbConnection().DataSource);

        // ---- Master data ----
        var company = await GetOrCreateCompanyAsync(db);
        var supplier = await GetOrCreateSupplierAsync(db, "SOLVEX FZE", "UAE");
        var unit = await GetOrCreateUnitAsync(db, "MT", "Metric Ton");
        var gasoline = await GetOrCreateProductAsync(db, "GASOLINE-A92", "Gasoline A-92", unit);
        var diesel = await GetOrCreateProductAsync(db, "DIESEL", "Diesel", unit);
        var srfNoorzad = await GetOrCreateSarrafAsync(db, "Noorzad Dubai");
        var srfMoneb = await GetOrCreateSarrafAsync(db, "Moneb Omar Habibi");
        var srfHabibi = await GetOrCreateSarrafAsync(db, "Habibi");

        // ---- Contracts ----
        var c570 = await GetOrCreateContractAsync(db, new ContractSpec(
            "PTG-SOL-25-MOZ-570", gasoline!.Id, unit!.Id, company!.Id, supplier!.Id,
            D(2025, 5, 23), 570m, 10000m,
            "Gasoline A-92 fixed 570 USD/MT. Base PTG-SOL-25-MOZ dd 23.05.2025; Additional Agreement #6 dd 14.01.2026; SPT TRUSOVO. Consignee Terminal Ilinka; Destination Trusowo.",
            new[] { ("6", D(2026, 1, 14), 10000m) }));
        var c610 = await GetOrCreateContractAsync(db, new ContractSpec(
            "PTG-SOL-25-MOZ-610", diesel!.Id, unit.Id, company.Id, supplier.Id,
            D(2025, 5, 23), 610m, 20000m,
            "Diesel fixed 610 USD/MT. Base PTG-SOL-25-MOZ dd 23.05.2025; Appendix #4 dd 16.12.2025 (10000 MT) + Appendix #5 dd 26.12.2025 (10000 MT); DAP TRUSOVO. Note: FLOWDALE TRADING FZCO referenced in file (not set as contract supplier without proof). Consignee Terminal Ilinka; Destination Trusowo.",
            new[] { ("4", D(2025, 12, 16), 10000m), ("5", D(2025, 12, 26), 10000m) }));
        var c600 = await GetOrCreateContractAsync(db, new ContractSpec(
            "PTG-SOL-25-MOZ-600", diesel.Id, unit.Id, company.Id, supplier.Id,
            D(2025, 6, 20), 600m, 20000m,
            "Diesel fixed 600 USD/MT. Base PTG-SOL-25-MOZ dd 20.06.2025; Appendix #6 dd 14.01.2026 (20000 MT); DAP TRUSOVO. File currently holds 9,967.65 MT loaded of the 20,000 MT contract. Consignee Terminal Ilinka; Destination Trusowo.",
            new[] { ("6", D(2026, 1, 14), 20000m) }));
        var c580 = await GetOrCreateContractAsync(db, new ContractSpec(
            "PTG-SOL-25-MOZ-580", diesel.Id, unit.Id, company.Id, supplier.Id,
            D(2025, 5, 23), 580m, 10000m,
            "Diesel fixed 580 USD/MT. Base PTG-SOL-25-MOZ dd 23.05.2025; Additional Agreement #7 dd 27.01.2026; SPT TRUSOVO. (Sheet 2026 mislabels as Gasoline 580; loading sheet is authoritative = Diesel.) Consignee Terminal Ilinka; Destination Trusowo.",
            new[] { ("7", D(2026, 1, 27), 10000m) }));

        var contractByKey = new Dictionary<string, Contract?>
        {
            ["570"] = c570, ["610"] = c610, ["600"] = c600, ["580"] = c580
        };
        var productByKey = new Dictionary<string, int>
        {
            ["570"] = gasoline.Id, ["610"] = diesel.Id, ["600"] = diesel.Id, ["580"] = diesel.Id
        };

        // ---- Loadings (805) ----
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_loadingsPath));
        foreach (var key in new[] { "570", "610", "600", "580" })
        {
            var sheet = doc.RootElement.GetProperty(key);
            var contract = contractByKey[key]!;
            var productId = productByKey[key];
            int made = 0, skip = 0;
            foreach (var row in sheet.GetProperty("rows").EnumerateArray())
            {
                var (m, s) = await UpsertLoadingAsync(db, contract, productId, row);
                made += m; skip += s;
            }
            Console.WriteLine($"  loadings {key}: +{made} created, {skip} existing");
        }

        // ---- Payments (via sarraf) ----
        // Single-rate supplier payments (supplier credit = RUB / SOLVEX rate; sarraf debt = same).
        var single = new List<ViaSarrafSingle>
        {
            new("6",    D(2026,1,14), srfMoneb!,   "610", 500_000_000m,        78.5711m,          null),
            new("92",   D(2026,1,23), srfNoorzad!, "570", 200_000_000m,        76.0204218011m,    null),
            new("1231", D(2026,2,17), srfHabibi!,  "570", 47_798_900.69m,      76.0204218011m,    "دو بخش حسابداری از یک رسید واقعی به مبلغ مجموع 303,400,000 RUB"),
            new("1231", D(2026,2,17), srfHabibi!,  "580", 255_601_099.31m,     76.8180357717m,    "دو بخش حسابداری از یک رسید واقعی به مبلغ مجموع 303,400,000 RUB"),
            new("74",   D(2026,1,27), srfMoneb!,   "600", 110_000_000m,        76.5519m,          null),
            new("76",   D(2026,1,27), srfMoneb!,   "600", 120_000_000m,        76.5519m,          null),
            new("78",   D(2026,1,27), srfMoneb!,   "600", 100_000_000m,        76.5519m,          null),
            new("135",  D(2026,1,30), srfNoorzad!, "600", 200_000_000m,        76.0251m,          null),
            new("398",  D(2026,3,18), srfNoorzad!, "580", 80_000_000m,         76.4643009833m,    null),
        };
        foreach (var p in single)
            await UpsertViaSarrafSingleAsync(db, p, contractByKey[p.ContractKey]!, supplier);

        // Dual-rate supplier payments with FX difference (company rate vs SOLVEX accepted rate).
        var dual = new List<ViaSarrafDual>
        {
            new("665", D(2025,12,23), srfNoorzad!, "610", 200_000_000m, 79.3146m, 76m),
            new("681", D(2025,12,29), srfNoorzad!, "610", 300_000_000m, 77.6923m, 76m),
        };
        foreach (var p in dual)
            await UpsertViaSarrafDualAsync(db, p, contractByKey[p.ContractKey]!, supplier);

        // ---- Receipt 47: BLOCKED (sarraf unknown; empty DB has no prior record to resolve) ----
        _blocked.Add("Payment receipt 47 (contract 570, 2026-01-19, RUB 188,664,149.65 @ 78.2575920741): sarraf NOT specified in file and no prior receipt-47 record exists in DB to resolve it. Held per instructions — not guessed.");

        // ---- Contract balance transfers 610->580 and 600->580: BLOCKED (workflow limitation) ----
        _blocked.Add("Contract balance transfer 610->580 (source 620,585.34 USD @76.0251 -> RUB 47,180,062.53 -> dest 617,020.78 USD @76.4643009833, conv. diff 3,564.56 USD): official ContractBalanceTransfer stores only one rate/one USD amount (equal debit=credit), cannot record source-rate + intermediate-RUB + dest-rate + conversion difference, and its available-balance direction rejects an overpayment. Not simulated with fake payments per instructions.");
        _blocked.Add("Contract balance transfer 600->580 (source 960,921.03 USD @76.0251 -> RUB 73,054,117.397853 -> dest 955,401.62 USD @76.4643009833, conv. diff 5,519.41 USD): same ContractBalanceTransfer limitation. Not simulated.");

        await ReportAsync(db, supplier, contractByKey);
    }

    // ---------------- master data ----------------

    async Task<Company?> GetOrCreateCompanyAsync(ApplicationDbContext db)
    {
        var existing = await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == "petrogaz trading");
        if (existing != null) { _skipped.Add($"Company '{existing.Name}' (Id={existing.Id})"); return existing; }
        var anyOwner = await db.Companies.AnyAsync(c => c.IsSystemOwner);
        var e = new Company { Code = "PETROGAZ", Name = "PETROGAZ TRADING", Country = "UAE", IsSystemOwner = !anyOwner, IsActive = true };
        return await AddAsync(db, e, $"Company '{e.Name}' (system owner={e.IsSystemOwner})");
    }

    async Task<Supplier?> GetOrCreateSupplierAsync(ApplicationDbContext db, string name, string country)
    {
        var existing = await db.Suppliers.FirstOrDefaultAsync(s => s.Name.ToLower() == name.ToLower());
        if (existing != null) { _skipped.Add($"Supplier '{existing.Name}' (Id={existing.Id})"); return existing; }
        var e = new Supplier { Name = name, Country = country, IsActive = true };
        return await AddAsync(db, e, $"Supplier '{name}'");
    }

    async Task<Unit?> GetOrCreateUnitAsync(ApplicationDbContext db, string code, string name)
    {
        var existing = await db.Units.FirstOrDefaultAsync(u => u.Code.ToLower() == code.ToLower());
        if (existing != null) { _skipped.Add($"Unit '{existing.Code}' (Id={existing.Id})"); return existing; }
        var e = new Unit { Code = code, Name = name, IsBaseUnit = true, IsActive = true };
        return await AddAsync(db, e, $"Unit '{code}'");
    }

    async Task<Product?> GetOrCreateProductAsync(ApplicationDbContext db, string code, string name, Unit? unit)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Name.ToLower() == name.ToLower());
        if (existing != null) { _skipped.Add($"Product '{existing.Name}' (Id={existing.Id})"); return existing; }
        var e = new Product { Code = code, Name = name, UnitId = unit?.Id, UnitOfMeasure = "MT", IsActive = true };
        return await AddAsync(db, e, $"Product '{name}'");
    }

    async Task<Sarraf?> GetOrCreateSarrafAsync(ApplicationDbContext db, string name)
    {
        var existing = await db.Sarrafs.FirstOrDefaultAsync(s => s.Name.ToLower() == name.ToLower());
        if (existing != null) { _skipped.Add($"Sarraf '{existing.Name}' (Id={existing.Id})"); return existing; }
        var e = new Sarraf { Name = name, IsActive = true };
        return await AddAsync(db, e, $"Sarraf '{name}'");
    }

    async Task<T?> AddAsync<T>(ApplicationDbContext db, T entity, string label) where T : class
    {
        if (!_write) { _created.Add("[would create] " + label); return entity; }
        db.Add(entity);
        await db.SaveChangesAsync();
        _created.Add(label);
        return entity;
    }

    // ---------------- contracts ----------------

    record ContractSpec(string Number, int ProductId, int UnitId, int CompanyId, int SupplierId,
        DateTime ContractDate, decimal UnitPriceUsd, decimal QuantityMt, string Notes,
        (string No, DateTime Date, decimal Qty)[] Appendices);

    async Task<Contract?> GetOrCreateContractAsync(ApplicationDbContext db, ContractSpec s)
    {
        var existing = await db.Contracts.FirstOrDefaultAsync(c => c.ContractNumber == s.Number);
        if (existing != null) { _skipped.Add($"Contract '{existing.ContractNumber}' (Id={existing.Id})"); return existing; }
        var e = new Contract
        {
            ContractNumber = s.Number,
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = s.CompanyId,
            ProductId = s.ProductId,
            UnitId = s.UnitId,
            SupplierId = s.SupplierId,
            ContractDate = s.ContractDate,
            PricingMethod = PricingMethod.Fixed,
            QuantityMt = s.QuantityMt,
            UnitPriceUsd = s.UnitPriceUsd,
            Currency = "USD",
            SettlementCurrencyCode = "USD",
            RubRatePolicy = RubSettlementRatePolicy.NotApplicable,
            Notes = s.Notes
        };
        if (!_write) { _created.Add($"[would create] Contract '{s.Number}' ({s.UnitPriceUsd} USD/MT, {s.QuantityMt} MT) + {s.Appendices.Length} amendment(s)"); return e; }
        db.Contracts.Add(e);
        await db.SaveChangesAsync();
        foreach (var (no, date, qty) in s.Appendices)
        {
            db.ContractAmendments.Add(new ContractAmendment
            {
                ContractId = e.Id,
                AmendmentDate = date,
                AmendmentNumber = no,
                ChangeSummary = $"Appendix/Additional Agreement #{no} dd {date:dd.MM.yyyy}, quantity {qty:N0} MT.",
                NewQuantityMt = qty,
                NewUnitPriceUsd = s.UnitPriceUsd
            });
        }
        await db.SaveChangesAsync();
        _created.Add($"Contract '{s.Number}' (Id={e.Id}, {s.UnitPriceUsd} USD/MT, {s.QuantityMt} MT) + {s.Appendices.Length} amendment(s)");
        return e;
    }

    // ---------------- loadings ----------------

    async Task<(int made, int skip)> UpsertLoadingAsync(ApplicationDbContext db, Contract contract, int productId, JsonElement row)
    {
        var date = DParse(row.GetProperty("date").GetString()!);
        var rwb = row.TryGetProperty("rwb", out var rwbEl) && rwbEl.ValueKind == JsonValueKind.String ? rwbEl.GetString() : null;
        var wagon = row.TryGetProperty("wagon", out var wEl) && wEl.ValueKind == JsonValueKind.String ? wEl.GetString() : null;
        var qty = row.GetProperty("qty").GetDecimal();
        var price = row.GetProperty("price").GetDecimal();
        var arrival = TryDate(row, "arrival");
        var leakDate = TryDate(row, "leak_date");
        var remarks = row.TryGetProperty("remarks", out var rem) && rem.ValueKind == JsonValueKind.String ? rem.GetString() : null;

        // Idempotency key: contract + date + RWB + wagon + quantity.
        var exists = await db.LoadingRegisters.AnyAsync(l =>
            l.ContractId == contract.Id &&
            l.LoadingDate == date &&
            l.RwbNo == rwb &&
            l.WagonNumber == wagon &&
            l.LoadedQuantityMt == qty);
        if (exists) { return (0, 1); }

        if (!_write) return (1, 0);

        var loading = new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = productId,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = date,
            LoadedQuantityMt = qty,
            RwbNo = Trunc(rwb, 100),
            WagonNumber = Trunc(wagon, 200),
            ConsigneeName = "Terminal Ilinka",
            DestinationName = "Trusowo",
            LoadingPriceUsd = price,
            SettlementCurrencyCode = "USD",
            RubRateStatus = RubSettlementRateStatus.NotRequired,
            Notes = string.IsNullOrWhiteSpace(remarks) ? null : Trunc(remarks, 1000)
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();

        // Supplier-payable ledger for this loading (official helper), idempotent on (SourceType, SourceId).
        if (SupplierLoadingLedger.IsPostable(loading, contract))
        {
            var already = await db.LedgerEntries.AnyAsync(l => l.SourceType == LoadingLedgerSource && l.SourceId == loading.Id);
            if (!already)
            {
                db.LedgerEntries.Add(SupplierLoadingLedger.Create(loading, contract));
                await db.SaveChangesAsync();
            }
        }
        return (1, 0);
    }

    static DateTime? TryDate(JsonElement row, string prop)
    {
        if (row.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }
        return null;
    }

    static string? Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] : s);

    // ---------------- payments ----------------

    record ViaSarrafSingle(string Receipt, DateTime Date, Sarraf Sarraf, string ContractKey, decimal Rub, decimal SolvexRate, string? SharedDescription);
    record ViaSarrafDual(string Receipt, DateTime Date, Sarraf Sarraf, string ContractKey, decimal Rub, decimal SolvexRate, decimal CompanyRate);

    async Task UpsertViaSarrafSingleAsync(ApplicationDbContext db, ViaSarrafSingle p, Contract contract, Supplier supplier)
    {
        var amountUsd = decimal.Round(p.Rub / p.SolvexRate, 4, MidpointRounding.AwayFromZero);
        var fxRateToUsd = decimal.Round(1m / p.SolvexRate, 6, MidpointRounding.AwayFromZero);
        var fxSource = $"Via sarraf manual rate: 1 USD = {p.SolvexRate:N4} RUB";
        var desc = p.SharedDescription ?? "پرداخت از طریق صراف برای تأمین‌کننده";
        var label = $"ViaSarraf single receipt {p.Receipt} / {p.ContractKey} / {p.Sarraf.Name} / RUB {p.Rub:N2} @ {p.SolvexRate} => USD {amountUsd:N4}";

        var exists = await db.LedgerEntries.AnyAsync(l =>
            l.SourceType == ViaSarrafPaymentSource &&
            l.SourceId == p.Sarraf.Id &&
            l.Reference == p.Receipt &&
            l.EntryDate == p.Date &&
            l.SourceAmount == p.Rub &&
            l.ContractId == contract.Id);
        if (exists) { _skipped.Add(label); return; }
        if (!_write) { _created.Add("[would create] " + label); return; }

        var groupId = Guid.NewGuid();
        var supplierLeg = new LedgerEntry
        {
            EntryDate = p.Date, Side = LedgerSide.Debit, AmountUsd = amountUsd, Currency = "USD",
            SourceAmount = p.Rub, SourceCurrencyCode = "RUB", AppliedFxRateToUsd = fxRateToUsd,
            AppliedFxRateDate = p.Date, AppliedFxRateSource = fxSource, Description = desc,
            SourceType = ViaSarrafPaymentSource, SourceId = p.Sarraf.Id, Reference = p.Receipt,
            SupplierId = supplier.Id, ContractId = contract.Id, ViaSarrafGroupId = groupId
        };
        var payableLeg = new LedgerEntry
        {
            EntryDate = p.Date, Side = LedgerSide.Credit, AmountUsd = amountUsd, Currency = "USD",
            SourceAmount = p.Rub, SourceCurrencyCode = "RUB", AppliedFxRateToUsd = fxRateToUsd,
            AppliedFxRateDate = p.Date, AppliedFxRateSource = fxSource,
            Description = $"بدهی شرکت به صراف بابت پرداخت به تأمین‌کننده: {desc}",
            SourceType = ViaSarrafPayableSource, SourceId = p.Sarraf.Id, Reference = p.Receipt,
            ContractId = contract.Id, ViaSarrafGroupId = groupId
        };
        db.LedgerEntries.AddRange(supplierLeg, payableLeg);
        await db.SaveChangesAsync();
        _created.Add(label + $" [group {groupId}]");
    }

    async Task UpsertViaSarrafDualAsync(ApplicationDbContext db, ViaSarrafDual p, Contract contract, Supplier supplier)
    {
        var creditUsd = decimal.Round(p.Rub / p.SolvexRate, 4, MidpointRounding.AwayFromZero);
        var companyUsd = decimal.Round(p.Rub / p.CompanyRate, 4, MidpointRounding.AwayFromZero);
        var diff = decimal.Round(companyUsd - creditUsd, 4, MidpointRounding.AwayFromZero);
        var label = $"ViaSarraf DUAL receipt {p.Receipt} / {p.ContractKey} / {p.Sarraf.Name} / RUB {p.Rub:N2} : SOLVEX credit USD {creditUsd:N4} @ {p.SolvexRate}, company USD {companyUsd:N4} @ {p.CompanyRate}, FX diff USD {diff:N4}";

        // Stable idempotency key: sarraf + receipt + date + contract (amounts are USD-derived).
        var matches = await db.SarrafSettlements
            .Where(s => s.SarrafId == p.Sarraf.Id && s.ReferenceNumber == p.Receipt
                     && s.SettlementDate == p.Date && s.ContractId == contract.Id)
            .OrderBy(s => s.Id).ToListAsync();
        if (matches.Count > 0)
        {
            // Self-heal any duplicates from an earlier bad run: keep the first, remove extras + their ledgers.
            if (matches.Count > 1 && _write)
            {
                foreach (var extra in matches.Skip(1))
                {
                    var legs = await db.LedgerEntries.Where(l =>
                        (l.SourceType == SarrafSettlementService.SupplierLedgerSourceType
                         || l.SourceType == SarrafSettlementService.ExchangeDifferenceSourceType)
                        && l.SourceId == extra.Id).ToListAsync();
                    db.LedgerEntries.RemoveRange(legs);
                    db.SarrafSettlements.Remove(extra);
                }
                await db.SaveChangesAsync();
                _created.Add($"[dedup] removed {matches.Count - 1} duplicate settlement(s) for receipt {p.Receipt}/{p.ContractKey}");
            }
            _skipped.Add(label + $" [kept settlement Id={matches[0].Id}]");
            return;
        }
        if (!_write) { _created.Add("[would create] " + label); return; }

        var svc = new SarrafSettlementService(db);
        // Amounts passed in USD (fx=1) so the SOLVEX credit and the rate difference are exact
        // (RUB*round(1/rate,6) would lose precision on 200-300M RUB). RUB origin kept in Description.
        // AcceptedOnly => supplier payable is reduced by the SOLVEX-accepted credit (creditUsd),
        // NOT the company amount, so SOLVEX is never over-credited; the difference is recorded on
        // the settlement (DifferenceAmountUsd / SupplierShortfall) as the rate difference.
        var cmd = new SarrafSettlementCommand(
            SettlementDate: p.Date,
            SarrafId: p.Sarraf.Id,
            SupplierId: supplier.Id,
            ContractId: contract.Id,
            PaymentTransactionId: null,
            CashAccountId: null,
            ReferenceNumber: p.Receipt,
            Description: $"پرداخت از طریق صراف برای تأمین‌کننده. RUB {p.Rub:N2} @ SOLVEX {p.SolvexRate} = اعتبار {creditUsd:N4} USD؛ نرخ واقعی شرکت {p.CompanyRate} = {companyUsd:N4} USD؛ تفاوت نرخ {diff:N4} USD.",
            RequestedAmount: companyUsd,
            RequestedCurrency: "USD",
            RequestedFxRateToUsd: 1m,
            SarrafCurrency: "USD",
            SarrafRate: 1m,
            SarrafChargedAmount: companyUsd,
            SarrafFxRateToUsd: 1m,
            SupplierAcceptedAmount: creditUsd,
            SupplierAcceptedCurrency: "USD",
            SupplierAcceptedFxRateToUsd: 1m,
            SupplierRate: p.SolvexRate,
            DifferenceTreatment: SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Direction: SarrafSettlementDirection.Out,
            CounterpartyType: SarrafSettlementCounterpartyType.Supplier);
        var settlement = await svc.CreatePostedAsync(cmd);
        _created.Add(label + $" [settlement Id={settlement.Id}, systemDiff USD {settlement.DifferenceAmountUsd:N4} ({settlement.DifferenceType})]");
    }

    // ---------------- report ----------------

    async Task ReportAsync(ApplicationDbContext db, Supplier supplier, Dictionary<string, Contract?> contracts)
    {
        Console.WriteLine();
        Console.WriteLine($"---------- {( _write ? "WRITE" : "DRY-RUN")} REPORT ----------");
        Console.WriteLine($"CREATED ({_created.Count}):");
        foreach (var s in _created) Console.WriteLine("  + " + s);
        Console.WriteLine($"SKIPPED / already-present ({_skipped.Count}):");
        foreach (var s in _skipped) Console.WriteLine("  = " + s);
        Console.WriteLine($"BLOCKED ({_blocked.Count}):");
        foreach (var s in _blocked) Console.WriteLine("  ! " + s);

        if (_write)
        {
            Console.WriteLine();
            Console.WriteLine("---------- RECONCILIATION (system ledger) ----------");
            foreach (var key in new[] { "570", "610", "600", "580" })
            {
                var c = contracts[key];
                if (c == null || c.Id == 0) { Console.WriteLine($"  contract {key}: (not persisted)"); continue; }
                var loadCount = await db.LoadingRegisters.CountAsync(l => l.ContractId == c.Id);
                var loadMt = await db.LoadingRegisters.Where(l => l.ContractId == c.Id).SumAsync(l => (decimal?)l.LoadedQuantityMt) ?? 0m;
                var loadVal = await db.LedgerEntries.Where(l => l.SourceType == LoadingLedgerSource && l.ContractId == c.Id).SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;
                var payDebit = await db.LedgerEntries.Where(l => l.SourceType == ViaSarrafPaymentSource && l.ContractId == c.Id).SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;
                var settleCredit = await db.LedgerEntries.Where(l => l.SourceType == SarrafSettlementService.SupplierLedgerSourceType && l.ContractId == c.Id).SumAsync(l => (decimal?)l.AmountUsd) ?? 0m;
                Console.WriteLine($"  {key}: loadings={loadCount} ({loadMt:N2} MT, value {loadVal:N2} USD); single-pay debit {payDebit:N2}; settlement-credit {settleCredit:N2} USD");
            }
            var supNet = await db.LedgerEntries.Where(l => l.SupplierId == supplier.Id)
                .SumAsync(l => (decimal?)(l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd)) ?? 0m;
            Console.WriteLine($"  SOLVEX supplier net (Credit-Debit) = {supNet:N2} USD  (positive = we still owe supplier)");
        }
        Console.WriteLine("---------- END ----------");
    }
}
