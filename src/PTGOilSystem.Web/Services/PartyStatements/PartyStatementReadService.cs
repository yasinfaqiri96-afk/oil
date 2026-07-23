using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;

namespace PTGOilSystem.Web.Services.PartyStatements;

public sealed class PartyStatementReadService : IPartyStatementReadService
{
    private const string BaseCurrency = "USD";
    private const string ViaSarrafPayableLedgerSourceType = LedgerEntryOwnership.ViaSarrafPayableSourceType;
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementPolicyResolver _policyResolver;
    private readonly PartyStatementOptions _options;

    public PartyStatementReadService(
        ApplicationDbContext db,
        IPartyStatementPolicyResolver policyResolver,
        IOptions<PartyStatementOptions> options)
    {
        _db = db;
        _policyResolver = policyResolver;
        _options = options.Value;
    }

    public async Task<PartyStatementResult> GetStatementAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        var policy = _policyResolver.Resolve(party.PartyType);
        var partyInfo = await LoadPartyInfoAsync(party, cancellationToken)
            ?? throw new KeyNotFoundException("طرف‌حساب موردنظر پیدا نشد.");

        var calculation = party.PartyType switch
        {
            PartyStatementPartyType.Partner => await BuildPartnerRowsAsync(party, filter, policy, cancellationToken),
            PartyStatementPartyType.Employee => await BuildEmployeeRowsAsync(party, filter, cancellationToken),
            PartyStatementPartyType.Sarraf => await BuildSarrafRowsAsync(party, filter, cancellationToken),
            _ => await BuildLedgerRowsAsync(party, filter, policy, cancellationToken)
        };

        if (filter.IncludeOperationalColumns && policy.SupportsOperationalColumns)
        {
            await AddOperationalColumnsAsync(calculation.PeriodRows, cancellationToken);
        }

        // نمایش روبلی: کاربر ارز روبل را انتخاب کرده تا مانده و جمع‌های روبلیِ واقعی
        // (به نرخ تاریخی هر سند) نمایش داده شوند. مقادیر USD دست‌نخورده باقی می‌مانند.
        var presentInRub = IsRubPresentation(filter);
        foreach (var row in calculation.PeriodRows)
        {
            ApplyRubValues(row);
        }

        var resultRows = BuildRunningRows(calculation.OpeningBalance, calculation.PeriodRows, filter.FromDate);
        var totalDebit = calculation.PeriodRows.Sum(r => r.DebitBase ?? 0m);
        var totalCredit = calculation.PeriodRows.Sum(r => r.CreditBase ?? 0m);
        var closing = calculation.OpeningBalance + totalCredit - totalDebit;

        // جمع‌ها و مانده جاری روبلی — فقط از اسناد روبلی؛ اسناد غیرروبلی ارزش روبلی
        // ندارند و در این محاسبه شرکت نمی‌کنند (در سطر «—» نمایش داده می‌شوند).
        decimal? openingRub = null, totalDebitRub = null, totalCreditRub = null, closingRub = null;
        if (presentInRub)
        {
            openingRub = calculation.OpeningBalance == 0m ? 0m : null;
            totalDebitRub = calculation.PeriodRows.Sum(r => r.DebitRub ?? 0m);
            totalCreditRub = calculation.PeriodRows.Sum(r => r.CreditRub ?? 0m);
            var runningRub = openingRub ?? 0m;
            foreach (var row in resultRows)
            {
                if (row.IsOpeningBalance)
                {
                    row.RunningBalanceRub = openingRub;
                    continue;
                }
                if (row.SignedAmountRub.HasValue)
                {
                    runningRub += row.SignedAmountRub.Value;
                    row.RunningBalanceRub = runningRub;
                }
            }
            closingRub = (openingRub ?? 0m) + totalCreditRub.Value - totalDebitRub.Value;
        }

        var companyInfo = await LoadCompanyInfoAsync(party, filter.ContractId, cancellationToken);
        var periodRows = resultRows.Where(r => !r.IsOpeningBalance).ToList();
        var periodFrom = filter.FromDate?.Date ?? periodRows.FirstOrDefault()?.Date.Date;
        var periodTo = filter.ToDate?.Date ?? periodRows.LastOrDefault()?.Date.Date ?? DateTime.UtcNow.Date;
        var displayCurrency = presentInRub ? "RUB" : NormalizeCurrency(_options.BaseCurrencyCode);

        return new PartyStatementResult
        {
            Party = party,
            Policy = policy,
            CompanyInfo = companyInfo,
            PartyInfo = partyInfo,
            DocumentInfo = new PartyStatementDocumentInfo
            {
                StatementNumber = BuildStatementNumber(party, periodTo),
                StatementDate = DateTime.UtcNow.Date,
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                BaseCurrencyCode = displayCurrency,
                GeneratedAtUtc = DateTime.UtcNow
            },
            Summary = new PartyStatementSummary
            {
                OpeningBalance = calculation.OpeningBalance,
                TotalDebit = totalDebit,
                TotalCredit = totalCredit,
                ClosingBalance = closing,
                ClosingBalanceMeaning = policy.BalanceMeaning(closing),
                BaseCurrencyCode = displayCurrency,
                IsRubPresentation = presentInRub,
                OpeningBalanceRub = openingRub,
                TotalDebitRub = totalDebitRub,
                TotalCreditRub = totalCreditRub,
                ClosingBalanceRub = closingRub
            },
            ColumnOptions = ResolveColumns(periodRows, filter),
            Rows = resultRows,
            Note = _options.Note,
            CourtesyText = _options.CourtesyText,
            Authorization = new PartyStatementAuthorization
            {
                AuthorizedByName = _options.AuthorizedByName,
                AuthorizedByTitle = _options.AuthorizedByTitle,
                SignatureImagePath = _options.SignatureImagePath
            }
        };
    }

    private async Task<StatementCalculation> BuildLedgerRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        PartyStatementPolicy policy,
        CancellationToken ct)
    {
        // سطرهای منسوخ/برگشتیِ تسویهٔ صراف (ثبت اصلی که با repost جایگزین شده و سطرِ برگشت)
        // نباید در صورت‌حساب رسمی جدا نمایش داده یا در جمع‌ها دوبار شمرده شوند؛ فقط اثرِ جاری
        // (سطری که تسویهٔ Posted به آن اشاره می‌کند) می‌ماند. رجوع: SarrafSettlementLedgerEffectiveness.
        var baseQuery = BuildPartyLedgerQuery(party, filter)
            .WhereEffectiveSarrafSettlementLegs(_db);
        var opening = 0m;
        if (filter.FromDate.HasValue)
        {
            var from = filter.FromDate.Value.Date;
            opening = await baseQuery
                .Where(l => l.EntryDate < from)
                .SumAsync(l => (decimal?)(l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd), ct)
                ?? 0m;
            if (policy.ReverseLegacyLedgerSides)
            {
                opening = -opening;
            }
        }

        var periodQuery = ApplyPeriod(baseQuery, filter);
        var entries = await periodQuery
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.CreatedAtUtc)
            .ThenBy(l => l.Id)
            .Select(l => new LedgerStatementProjection
            {
                Id = l.Id,
                Date = l.EntryDate,
                CreatedAtUtc = l.CreatedAtUtc,
                Side = l.Side,
                AmountUsd = l.AmountUsd,
                OriginalAmount = l.SourceAmount ?? l.AmountUsd,
                OriginalCurrency = l.SourceCurrencyCode ?? l.Currency,
                FxRateToUsd = l.AppliedFxRateToUsd,
                Reference = l.Reference,
                Description = l.Description,
                SourceType = l.SourceType,
                SourceId = l.SourceId,
                ContractId = l.ContractId,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null
            })
            .ToListAsync(ct);

        var rows = entries.Select(e => MapLedgerRow(e, policy)).ToList();
        return new StatementCalculation(opening, rows);
    }

    private IQueryable<LedgerEntry> BuildPartyLedgerQuery(PartyRef party, PartyStatementFilter filter)
    {
        var query = _db.LedgerEntries.AsNoTracking().AsQueryable();

        query = party.PartyType switch
        {
            PartyStatementPartyType.Customer => query.Where(l =>
                l.CustomerId == party.PartyId
                || (l.CustomerId == null && l.Contract != null && l.Contract.CustomerId == party.PartyId)
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CustomerId == party.PartyId))),
            // انتساب تأمین‌کننده از تعریف مرکزی می‌آید تا اسنادِ متعلق به طرف‌حسابِ دیگر
            // (مثلاً کرایهٔ حملِ ServiceProvider/Driver روی همان قرارداد خرید) وارد
            // صورت‌حساب تأمین‌کننده نشوند. رجوع: LedgerEntryOwnership.SupplierOwned.
            PartyStatementPartyType.Supplier => query.Where(LedgerEntryOwnership.SupplierOwned(party.PartyId)),
            PartyStatementPartyType.ServiceProvider => query.Where(l => l.ServiceProviderId == party.PartyId),
            PartyStatementPartyType.Driver => query.Where(l => l.DriverId == party.PartyId),
            PartyStatementPartyType.Company => query.Where(l =>
                (l.Contract != null && l.Contract.CompanyId == party.PartyId)
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CompanyId == party.PartyId))),
            _ => throw new ArgumentOutOfRangeException(nameof(party), party.PartyType, "این نوع از Ledger عمومی خوانده نمی‌شود.")
        };

        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            query = query.Where(l =>
                l.ContractId == contractId
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.ContractId == contractId)));
        }

        if (party.CompanyId.HasValue && party.PartyType != PartyStatementPartyType.Company)
        {
            var companyId = party.CompanyId.Value;
            query = query.Where(l =>
                (l.Contract != null && l.Contract.CompanyId == companyId)
                || (l.SourceType == "Sale" && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.CompanyId == companyId)));
        }

        // در نمایش روبلی فیلتر ارز اعمال نمی‌شود تا همهٔ اسناد دیده شوند؛ ارزش روبلی
        // بعداً per-row محاسبه می‌گردد. برای سایر ارزها رفتار فیلتر بدون تغییر است.
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null && !IsRubPresentation(filter))
        {
            query = query.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == currency)
                || (l.SourceCurrencyCode == null && l.Currency == currency));
        }

        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < exclusiveEnd);
        }

        return query;
    }

    private static IQueryable<LedgerEntry> ApplyPeriod(IQueryable<LedgerEntry> query, PartyStatementFilter filter)
    {
        if (filter.FromDate.HasValue)
        {
            var from = filter.FromDate.Value.Date;
            query = query.Where(l => l.EntryDate >= from);
        }

        return query;
    }

    private static PartyStatementRow MapLedgerRow(LedgerStatementProjection entry, PartyStatementPolicy policy)
    {
        var ledgerDebit = entry.Side == LedgerSide.Debit ? entry.AmountUsd : (decimal?)null;
        var ledgerCredit = entry.Side == LedgerSide.Credit ? entry.AmountUsd : (decimal?)null;
        var debit = policy.ReverseLegacyLedgerSides ? ledgerCredit : ledgerDebit;
        var credit = policy.ReverseLegacyLedgerSides ? ledgerDebit : ledgerCredit;
        var currency = NormalizeCurrency(entry.OriginalCurrency);

        return new PartyStatementRow
        {
            Date = entry.Date,
            CreatedAtUtc = entry.CreatedAtUtc,
            Reference = entry.Reference,
            Description = entry.Description,
            DebitBase = debit,
            CreditBase = credit,
            OriginalAmount = entry.OriginalAmount,
            OriginalCurrency = currency,
            FxRate = ResolveHistoricalRate(entry.FxRateToUsd, currency),
            FxRateDisplay = PartyStatementFormatting.FxDisplay(entry.FxRateToUsd, currency),
            SourceType = entry.SourceType,
            SourceId = entry.SourceId,
            PostingSequence = entry.Id,
            ContractId = entry.ContractId,
            ContractNumber = entry.ContractNumber
        };
    }

    private async Task<StatementCalculation> BuildPartnerRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        PartyStatementPolicy policy,
        CancellationToken ct)
    {
        var sharesQuery = _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.PartnerId == party.PartyId);
        if (party.CompanyId.HasValue)
        {
            sharesQuery = sharesQuery.Where(cp => cp.Contract != null && cp.Contract.CompanyId == party.CompanyId.Value);
        }
        if (filter.ContractId.HasValue)
        {
            sharesQuery = sharesQuery.Where(cp => cp.ContractId == filter.ContractId.Value);
        }

        var shares = await sharesQuery
            .Select(cp => new { cp.ContractId, cp.SharePercent })
            .ToListAsync(ct);
        if (shares.Count == 0)
        {
            return new StatementCalculation(0m, []);
        }

        var shareByContract = shares.ToDictionary(x => x.ContractId, x => x.SharePercent);
        var contractIds = shareByContract.Keys.ToList();
        var saleMap = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .Select(s => new { s.Id, ContractId = s.ContractId!.Value })
            .ToDictionaryAsync(x => x.Id, x => x.ContractId, ct);
        var saleIds = saleMap.Keys.ToList();

        var query = _db.LedgerEntries
            .AsNoTracking()
            .Where(l =>
                (l.ContractId.HasValue && contractIds.Contains(l.ContractId.Value))
                || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId)))
            // اثرِ جاریِ تسویهٔ صراف: ثبت‌های منسوخ/برگشتی از سهمِ شریک هم کنار می‌روند.
            .WhereEffectiveSarrafSettlementLegs(_db);
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null && !IsRubPresentation(filter))
        {
            query = query.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == currency)
                || (l.SourceCurrencyCode == null && l.Currency == currency));
        }
        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < exclusiveEnd);
        }

        var entries = await query
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.CreatedAtUtc)
            .ThenBy(l => l.Id)
            .Select(l => new LedgerStatementProjection
            {
                Id = l.Id,
                Date = l.EntryDate,
                CreatedAtUtc = l.CreatedAtUtc,
                Side = l.Side,
                AmountUsd = l.AmountUsd,
                OriginalAmount = l.SourceAmount ?? l.AmountUsd,
                OriginalCurrency = l.SourceCurrencyCode ?? l.Currency,
                FxRateToUsd = l.AppliedFxRateToUsd,
                Reference = l.Reference,
                Description = l.Description,
                SourceType = l.SourceType,
                SourceId = l.SourceId,
                ContractId = l.ContractId,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null
            })
            .ToListAsync(ct);

        var allRows = new List<PartyStatementRow>();
        foreach (var entry in entries)
        {
            var contractId = entry.ContractId
                ?? (entry.SourceType == "Sale" && saleMap.TryGetValue(entry.SourceId, out var saleContractId)
                    ? saleContractId
                    : (int?)null);
            if (!contractId.HasValue || !shareByContract.TryGetValue(contractId.Value, out var sharePercent))
            {
                continue;
            }

            var ratio = sharePercent / 100m;
            entry.AmountUsd = decimal.Round(entry.AmountUsd * ratio, 2, MidpointRounding.AwayFromZero);
            entry.OriginalAmount = decimal.Round(entry.OriginalAmount * ratio, 4, MidpointRounding.AwayFromZero);
            entry.ContractId = contractId;
            allRows.Add(MapLedgerRow(entry, policy));
        }

        var from = filter.FromDate?.Date;
        var opening = from.HasValue ? allRows.Where(r => r.Date < from.Value).Sum(r => r.SignedAmount) : 0m;
        var periodRows = from.HasValue ? allRows.Where(r => r.Date >= from.Value).ToList() : allRows;
        return new StatementCalculation(opening, periodRows);
    }

    private async Task<StatementCalculation> BuildEmployeeRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var query = _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Where(t => t.EmployeeId == party.PartyId && !t.IsCancelled);
        var currency = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currency is not null && !IsRubPresentation(filter))
        {
            query = query.Where(t => t.Currency == currency);
        }
        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(t => t.TransactionDate < exclusiveEnd);
        }

        var transactions = await query
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);
        var allRows = transactions.Select(MapEmployeeRow).ToList();
        var from = filter.FromDate?.Date;
        var opening = from.HasValue ? allRows.Where(r => r.Date < from.Value).Sum(r => r.SignedAmount) : 0m;
        var periodRows = from.HasValue ? allRows.Where(r => r.Date >= from.Value).ToList() : allRows;
        return new StatementCalculation(opening, periodRows);
    }

    private static PartyStatementRow MapEmployeeRow(EmployeeSalaryTransaction transaction)
    {
        var amount = Math.Abs(transaction.AmountUsd);
        var increasesPayable = transaction.TransactionType is EmployeeSalaryTransactionType.SalaryAccrual
            or EmployeeSalaryTransactionType.Bonus
            || transaction.TransactionType == EmployeeSalaryTransactionType.Adjustment && transaction.AmountUsd > 0m;
        var currency = NormalizeCurrency(transaction.Currency);

        return new PartyStatementRow
        {
            Date = transaction.TransactionDate,
            CreatedAtUtc = transaction.CreatedAtUtc,
            Reference = transaction.Reference,
            Description = string.IsNullOrWhiteSpace(transaction.Description)
                ? EmployeeTransactionDescription(transaction.TransactionType)
                : transaction.Description,
            DebitBase = increasesPayable ? null : amount,
            CreditBase = increasesPayable ? amount : null,
            OriginalAmount = Math.Abs(transaction.Amount),
            OriginalCurrency = currency,
            FxRate = ResolveHistoricalRate(transaction.AppliedFxRateToUsd, currency),
            FxRateDisplay = PartyStatementFormatting.FxDisplay(transaction.AppliedFxRateToUsd, currency),
            SourceType = transaction.TransactionType.ToString(),
            SourceId = transaction.Id,
            PostingSequence = transaction.Id
        };
    }

    private async Task<StatementCalculation> BuildSarrafRowsAsync(
        PartyRef party,
        PartyStatementFilter filter,
        CancellationToken ct)
    {
        var allRows = new List<PartyStatementRow>();
        var settlementsQuery = _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.SarrafId == party.PartyId && s.Status == SarrafSettlementStatus.Posted);
        var paymentsQuery = _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SarrafId == party.PartyId);
        var viaQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == ViaSarrafPayableLedgerSourceType && l.SourceId == party.PartyId);
        var currencyFilter = NormalizeOptionalCurrency(filter.CurrencyCode);
        if (currencyFilter is not null && !IsRubPresentation(filter))
        {
            settlementsQuery = settlementsQuery.Where(s => s.SarrafCurrency == currencyFilter);
            paymentsQuery = paymentsQuery.Where(p => p.Currency == currencyFilter);
            viaQuery = viaQuery.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == currencyFilter)
                || (l.SourceCurrencyCode == null && l.Currency == currencyFilter));
        }

        if (filter.ToDate.HasValue)
        {
            var exclusiveEnd = filter.ToDate.Value.Date.AddDays(1);
            settlementsQuery = settlementsQuery.Where(s => s.SettlementDate < exclusiveEnd);
            paymentsQuery = paymentsQuery.Where(p => p.PaymentDate < exclusiveEnd);
            viaQuery = viaQuery.Where(l => l.EntryDate < exclusiveEnd);
        }
        if (filter.ContractId.HasValue)
        {
            settlementsQuery = settlementsQuery.Where(s => s.ContractId == filter.ContractId.Value);
            viaQuery = viaQuery.Where(l => l.ContractId == filter.ContractId.Value);
            paymentsQuery = paymentsQuery.Where(p => p.ContractId == filter.ContractId.Value);
        }
        if (party.CompanyId.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(p => p.CompanyId == party.CompanyId.Value);
            settlementsQuery = settlementsQuery.Where(s => s.Contract != null && s.Contract.CompanyId == party.CompanyId.Value);
            viaQuery = viaQuery.Where(l => l.Contract != null && l.Contract.CompanyId == party.CompanyId.Value);
        }

        var settlements = await settlementsQuery.ToListAsync(ct);
        foreach (var settlement in settlements)
        {
            var currency = NormalizeCurrency(settlement.SarrafCurrency);
            var isPayableIncrease = settlement.Direction == SarrafSettlementDirection.Out;
            allRows.Add(new PartyStatementRow
            {
                Date = settlement.SettlementDate,
                CreatedAtUtc = settlement.CreatedAtUtc,
                Reference = settlement.ReferenceNumber,
                Description = string.IsNullOrWhiteSpace(settlement.Description)
                    ? (isPayableIncrease ? "پرداخت صراف از طرف شرکت" : "دریافت صراف برای شرکت")
                    : settlement.Description,
                DebitBase = isPayableIncrease ? null : settlement.SarrafChargedAmountUsd,
                CreditBase = isPayableIncrease ? settlement.SarrafChargedAmountUsd : null,
                OriginalAmount = settlement.SarrafChargedAmount,
                OriginalCurrency = currency,
                FxRate = ResolveHistoricalRate(settlement.SarrafFxRateToUsd, currency),
                FxRateDisplay = PartyStatementFormatting.FxDisplay(settlement.SarrafFxRateToUsd, currency),
                SourceType = nameof(SarrafSettlement),
                SourceId = settlement.Id,
                PostingSequence = settlement.Id,
                ContractId = settlement.ContractId
            });
        }

        var viaRows = await viaQuery.ToListAsync(ct);
        allRows.AddRange(viaRows.Select(ledger => new PartyStatementRow
        {
            Date = ledger.EntryDate,
            CreatedAtUtc = ledger.CreatedAtUtc,
            Reference = ledger.Reference,
            Description = ledger.Description,
            CreditBase = ledger.AmountUsd,
            OriginalAmount = ledger.SourceAmount ?? ledger.AmountUsd,
            OriginalCurrency = NormalizeCurrency(ledger.SourceCurrencyCode ?? ledger.Currency),
            FxRate = ResolveHistoricalRate(ledger.AppliedFxRateToUsd, ledger.SourceCurrencyCode ?? ledger.Currency),
            FxRateDisplay = PartyStatementFormatting.FxDisplay(ledger.AppliedFxRateToUsd, ledger.SourceCurrencyCode ?? ledger.Currency),
            SourceType = ledger.SourceType,
            SourceId = ledger.Id,
            PostingSequence = ledger.Id,
            ContractId = ledger.ContractId
        }));

        var payments = await paymentsQuery.ToListAsync(ct);
        foreach (var payment in payments)
        {
            var isPaymentToSarraf = payment.Direction == PaymentDirection.Out;
            var currency = NormalizeCurrency(payment.Currency);
            allRows.Add(new PartyStatementRow
            {
                Date = payment.PaymentDate,
                CreatedAtUtc = payment.CreatedAtUtc,
                Reference = payment.Reference,
                Description = string.IsNullOrWhiteSpace(payment.Description)
                    ? (isPaymentToSarraf ? "پرداخت شرکت به صراف" : "برگشت وجه از صراف")
                    : payment.Description,
                DebitBase = isPaymentToSarraf ? payment.AmountUsd : null,
                CreditBase = isPaymentToSarraf ? null : payment.AmountUsd,
                OriginalAmount = payment.Amount,
                OriginalCurrency = currency,
                FxRate = ResolveHistoricalRate(payment.AppliedFxRateToUsd, currency),
                FxRateDisplay = PartyStatementFormatting.FxDisplay(payment.AppliedFxRateToUsd, currency),
                SourceType = nameof(PaymentTransaction),
                SourceId = payment.Id,
                PostingSequence = payment.Id,
                ContractId = payment.ContractId
            });
        }

        var from = filter.FromDate?.Date;
        var opening = from.HasValue ? allRows.Where(r => r.Date < from.Value).Sum(r => r.SignedAmount) : 0m;
        var periodRows = from.HasValue ? allRows.Where(r => r.Date >= from.Value).ToList() : allRows;
        return new StatementCalculation(opening, periodRows);
    }

    private async Task AddOperationalColumnsAsync(List<PartyStatementRow> rows, CancellationToken ct)
    {
        var saleIds = rows.Where(r => r.SourceType == "Sale").Select(r => r.SourceId).Distinct().ToList();
        if (saleIds.Count > 0)
        {
            var sales = await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => saleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.QuantityMt, s.UnitPriceUsd })
                .ToDictionaryAsync(s => s.Id, ct);
            foreach (var row in rows.Where(r => r.SourceType == "Sale"))
            {
                if (sales.TryGetValue(row.SourceId, out var sale))
                {
                    row.Quantity = sale.QuantityMt;
                    row.QuantityUnit = "MT";
                    row.UnitPrice = sale.UnitPriceUsd;
                }
            }
        }

        var loadingIds = rows.Where(r => r.SourceType == "Loading").Select(r => r.SourceId).Distinct().ToList();
        if (loadingIds.Count > 0)
        {
            var loadings = await _db.LoadingRegisters
                .AsNoTracking()
                .Where(l => loadingIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    l.LoadedQuantityMt,
                    l.PlattsUsd,
                    l.LoadingPriceUsd,
                    Premium = l.Contract != null ? l.Contract.PremiumDiscountUsd : null
                })
                .ToDictionaryAsync(l => l.Id, ct);
            foreach (var row in rows.Where(r => r.SourceType == "Loading"))
            {
                if (loadings.TryGetValue(row.SourceId, out var loading))
                {
                    row.Quantity = loading.LoadedQuantityMt;
                    row.QuantityUnit = "MT";
                    row.PlattsPrice = loading.PlattsUsd;
                    row.PremiumOrDiscount = loading.Premium;
                    row.UnitPrice = loading.LoadingPriceUsd;
                }
            }
        }
    }

    private async Task<PartyStatementPartyInfo?> LoadPartyInfoAsync(PartyRef party, CancellationToken ct)
        => party.PartyType switch
        {
            PartyStatementPartyType.Customer => await _db.Customers.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Supplier => await _db.Suppliers.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.ServiceProvider => await _db.ServiceProviders.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.Name, Code = x.Code, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Sarraf => await _db.Sarrafs.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.Name, Code = null, Phone = x.PhoneNumber, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Employee => await _db.Employees.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.FullName, Code = x.EmployeeCode, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Partner => await _db.Partners.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Phone = x.Phone, Email = x.Email, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Driver => await _db.Drivers.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.FullName, Code = x.LicenseNumber, Phone = x.Phone, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            PartyStatementPartyType.Company => await _db.Companies.AsNoTracking()
                .Where(x => x.Id == party.PartyId)
                .Select(x => new PartyStatementPartyInfo { Id = x.Id, Name = x.NamePersian ?? x.Name, Code = x.Code, Address = x.Address })
                .FirstOrDefaultAsync(ct),
            _ => null
        };

    private async Task<PartyStatementCompanyInfo> LoadCompanyInfoAsync(
        PartyRef party,
        int? contractId,
        CancellationToken ct)
    {
        int? companyId = party.PartyType == PartyStatementPartyType.Company ? party.PartyId : party.CompanyId;
        if (!companyId.HasValue && contractId.HasValue)
        {
            companyId = await _db.Contracts.AsNoTracking()
                .Where(c => c.Id == contractId.Value)
                .Select(c => (int?)c.CompanyId)
                .FirstOrDefaultAsync(ct);
        }

        var company = companyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId.Value)
                .Select(c => new { Name = c.NamePersian ?? c.Name, c.Address })
                .FirstOrDefaultAsync(ct)
            : null;

        return new PartyStatementCompanyInfo
        {
            Name = company?.Name ?? _options.CompanyName,
            Subtitle = _options.CompanySubtitle,
            Address = company?.Address ?? _options.Address,
            Phone = _options.Phone,
            Email = _options.Email,
            Website = _options.Website,
            LogoPath = _options.LogoPath
        };
    }

    private static List<PartyStatementRow> BuildRunningRows(
        decimal opening,
        List<PartyStatementRow> periodRows,
        DateTime? fromDate)
    {
        var ordered = periodRows
            .OrderBy(r => r.Date)
            .ThenBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.PostingSequence)
            .ThenBy(r => r.SourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceId)
            .ToList();
        var result = new List<PartyStatementRow>(ordered.Count + 1);
        var balance = opening;

        if (fromDate.HasValue || opening != 0m)
        {
            result.Add(new PartyStatementRow
            {
                Sequence = 0,
                Date = fromDate?.Date ?? ordered.FirstOrDefault()?.Date.Date ?? DateTime.UtcNow.Date,
                CreatedAtUtc = DateTime.MinValue,
                Reference = "OB",
                Description = "مانده ابتدایی",
                RunningBalance = opening,
                OriginalCurrency = BaseCurrency,
                SourceType = "OpeningBalance",
                IsOpeningBalance = true
            });
        }

        var sequence = 1;
        foreach (var row in ordered)
        {
            balance += row.SignedAmount;
            row.Sequence = sequence++;
            row.RunningBalance = balance;
            result.Add(row);
        }

        return result;
    }

    private static PartyStatementColumnOptions ResolveColumns(
        IReadOnlyCollection<PartyStatementRow> rows,
        PartyStatementFilter filter)
    {
        var currencies = rows
            .Select(r => NormalizeCurrency(r.OriginalCurrency))
            .Where(c => c != BaseCurrency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var showOperational = filter.IncludeOperationalColumns;

        return new PartyStatementColumnOptions
        {
            ShowRub = currencies.Contains("RUB", StringComparer.OrdinalIgnoreCase),
            ShowAed = currencies.Contains("AED", StringComparer.OrdinalIgnoreCase),
            ShowOriginalAmount = currencies.Any(c => c is not "RUB" and not "AED"),
            ShowCurrency = currencies.Count > 0,
            ShowFxRate = rows.Any(r => !string.Equals(r.OriginalCurrency, BaseCurrency, StringComparison.OrdinalIgnoreCase)),
            ShowQuantity = showOperational && rows.Any(r => r.Quantity.HasValue),
            ShowPlatts = showOperational && rows.Any(r => r.PlattsPrice.HasValue),
            ShowPremiumOrDiscount = showOperational && rows.Any(r => r.PremiumOrDiscount.HasValue),
            ShowUnitPrice = showOperational && rows.Any(r => r.UnitPrice.HasValue)
        };
    }

    private static void ValidateFilter(PartyStatementFilter filter)
    {
        if (filter.FromDate.HasValue && filter.ToDate.HasValue && filter.FromDate.Value.Date > filter.ToDate.Value.Date)
        {
            throw new ArgumentException("تاریخ شروع نمی‌تواند بعد از تاریخ پایان باشد.", nameof(filter));
        }
    }

    private static decimal? ResolveHistoricalRate(decimal? fxRateToUsd, string? currency)
        => string.Equals(currency, BaseCurrency, StringComparison.OrdinalIgnoreCase)
            ? 1m
            : fxRateToUsd is > 0m ? fxRateToUsd : null;

    // آیا کاربر روبل را برای «نمایش» انتخاب کرده؟ در این حالت به‌جای فیلترِ ارز،
    // همهٔ اسناد نمایش داده می‌شوند و ارزش روبلی هر سند (نرخ تاریخی خودش) محاسبه می‌شود.
    private static bool IsRubPresentation(PartyStatementFilter filter)
        => string.Equals(NormalizeOptionalCurrency(filter.CurrencyCode), "RUB", StringComparison.OrdinalIgnoreCase);

    // ارزش روبلی سطر: فقط برای اسناد ذاتاً روبلی (OriginalCurrency == RUB) که مبلغ
    // اصلی روبلی‌شان ذخیره شده است. سایر اسناد ارزش روبلیِ تاریخی ندارند و null می‌مانند.
    private static void ApplyRubValues(PartyStatementRow row)
    {
        if (!string.Equals(row.OriginalCurrency, "RUB", StringComparison.OrdinalIgnoreCase)
            || !row.OriginalAmount.HasValue)
        {
            return;
        }

        var amount = Math.Abs(row.OriginalAmount.Value);
        row.DebitRub = row.DebitBase.HasValue ? amount : null;
        row.CreditRub = row.CreditBase.HasValue ? amount : null;
    }

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? BaseCurrency : currency.Trim().ToUpperInvariant();

    private static string? NormalizeOptionalCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? null : NormalizeCurrency(currency);

    private static string BuildStatementNumber(PartyRef party, DateTime statementDate)
        => $"STMT-{party.PartyType.ToString()[..3].ToUpperInvariant()}-{party.PartyId:000000}-{statementDate:yyyyMMdd}";

    private static string EmployeeTransactionDescription(EmployeeSalaryTransactionType type)
        => type switch
        {
            EmployeeSalaryTransactionType.SalaryAccrual => "ثبت معاش دوره",
            EmployeeSalaryTransactionType.SalaryPayment => "پرداخت معاش",
            EmployeeSalaryTransactionType.SalaryAdvance => "پیش‌پرداخت معاش",
            EmployeeSalaryTransactionType.SalaryDeduction => "کسر معاش",
            EmployeeSalaryTransactionType.Bonus => "بونس",
            EmployeeSalaryTransactionType.Adjustment => "اصلاح حساب",
            _ => "تراکنش معاش"
        };

    private sealed record StatementCalculation(decimal OpeningBalance, List<PartyStatementRow> PeriodRows);

    private sealed class LedgerStatementProjection
    {
        public int Id { get; init; }
        public DateTime Date { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public LedgerSide Side { get; init; }
        public decimal AmountUsd { get; set; }
        public decimal OriginalAmount { get; set; }
        public string OriginalCurrency { get; init; } = BaseCurrency;
        public decimal? FxRateToUsd { get; init; }
        public string? Reference { get; init; }
        public string Description { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public int SourceId { get; init; }
        public int? ContractId { get; set; }
        public string? ContractNumber { get; init; }
    }
}
