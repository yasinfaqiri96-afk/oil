using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record ProfitAllocationResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IPartnershipProfitAllocationAdapter
{
    Task<ProfitAllocationResult> TryPostAllocationAsync(
        int contractId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Appropriates a partnership contract's book profit to the partners who own it.
///
///   Dr Retained Earnings
///   Cr Partner Current  (one line per partner, PartyType Partner)
///
/// Why Retained Earnings and not Current Year Earnings: 3100 belongs to
/// <see cref="FinalCloseService"/>, which derives it from the revenue and expense accounts when
/// the fiscal year closes and then moves it into 3200. Appropriating out of 3100 would fight that
/// calculation. Appropriating out of 3200 does not: the partners' claim is recognised now, the
/// year-end transfer credits 3200 with the same profit later, and 3200 ends at nil — which is
/// exactly what a partnership that distributes everything it earns should show. Nothing here
/// touches a revenue or expense account, so the P&amp;L is unchanged and the profit is not
/// recognised twice.
///
/// The per-partner amounts are not recomputed here. They come from
/// <see cref="IPartnershipStatementService.BuildForContractAsync"/> — the same figures the
/// partnership statement shows — so the ledger and the subledger cannot disagree about a partner's
/// share, and the residual cent lands on the same partner in both.
///
/// Idempotency: keyed by contract. A second run finds the journal and reports Duplicate. If the
/// contract's book profit has since moved, the allocation is deliberately <em>not</em> re-posted;
/// it skips with PROFIT_CHANGED_SINCE_ALLOCATION so a human decides whether to reverse and
/// re-allocate, rather than the ledger quietly holding two different answers.
/// </summary>
public sealed class PartnershipProfitAllocationAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    IPartnershipStatementService statements,
    IOptions<AccountingOptions> options,
    ILogger<PartnershipProfitAllocationAdapter> logger) : IPartnershipProfitAllocationAdapter
{
    public const string SourceModule = "PartnershipProfitAllocation";
    public const string SourceEntityType = nameof(Contract);

    private readonly AccountingOptions _options = options.Value;

    public static string BuildSourceEventId(int contractId)
        => $"PartnershipProfitAllocation:{contractId}:Allocated";

    public async Task<ProfitAllocationResult> TryPostAllocationAsync(
        int contractId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Skipped(contractId, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.PartnershipProfitAllocation)
            return Skipped(contractId, "PILOT_DISABLED");

        var contract = await db.Contracts
            .AsNoTracking()
            .Where(x => x.Id == contractId)
            .Select(x => new { x.Id, x.CompanyId, x.OwnershipType })
            .SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
            return Skipped(contractId, "CONTRACT_NOT_FOUND");
        if (contract.OwnershipType != ContractOwnershipType.Partnership)
            return Skipped(contractId, "CONTRACT_NOT_PARTNERSHIP");

        var companyId = contract.CompanyId;
        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken);
        if (settings is null)
            return Skipped(contractId, "ACCOUNTING_SETTINGS_MISSING");
        if (settings.PartnerCurrentAccountId is null or <= 0)
            return Skipped(contractId, PaymentAccountingAdapter.PartnerCurrentMissingSkipReason);

        var statement = await statements.BuildForContractAsync(contractId, cancellationToken);
        if (statement is null)
            return Skipped(contractId, "CONTRACT_STATEMENT_UNAVAILABLE");

        var profitUsd = statement.BookProfitUsd;
        if (profitUsd == 0m)
            return Skipped(contractId, "NO_PROFIT_TO_ALLOCATE");

        var shares = statement.Partners
            .Where(x => x.ProfitShareUsd != 0m)
            .OrderBy(x => x.PartnerId)
            .ToList();
        if (shares.Count == 0)
            return Skipped(contractId, "NO_PARTNER_SHARE");

        // The whole point of the shared allocation policy: what the partners are credited must be
        // the profit itself, to the cent. A mismatch here means the two ever drifted apart, and
        // posting anyway would put a plug in the ledger.
        var allocated = shares.Sum(x => x.ProfitShareUsd);
        if (allocated != profitUsd)
            return Skipped(contractId, "ALLOCATION_DOES_NOT_SUM_TO_PROFIT");

        var sourceEventId = BuildSourceEventId(contractId);
        var existing = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);
        if (existing is not null)
        {
            var postedProfit = existing.Lines.Sum(x => x.Debit);
            if (postedProfit != Math.Abs(profitUsd))
            {
                LogOutcome(contractId, companyId, profitUsd,
                    PaymentPostingStatus.Skipped, "PROFIT_CHANGED_SINCE_ALLOCATION");
                return new ProfitAllocationResult(
                    PaymentPostingStatus.Skipped, existing, "PROFIT_CHANGED_SINCE_ALLOCATION");
            }

            LogOutcome(contractId, companyId, profitUsd,
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new ProfitAllocationResult(
                PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        // A loss allocates the same way with both sides turned round: the partners absorb it and
        // Retained Earnings is credited back.
        var isProfit = profitUsd > 0m;
        var lines = new List<AccountingPostLine>
        {
            new(
                settings.RetainedEarningsAccountId,
                Debit: isProfit ? Math.Abs(profitUsd) : 0m,
                Credit: isProfit ? 0m : Math.Abs(profitUsd),
                SystemCurrency.BaseCurrencyCode,
                Math.Abs(profitUsd),
                1m,
                ContractId: contractId,
                Description: "Contract profit appropriated to partners")
        };

        foreach (var share in shares)
        {
            var amount = Math.Abs(share.ProfitShareUsd);
            var creditPartner = share.ProfitShareUsd > 0m;
            lines.Add(new AccountingPostLine(
                settings.PartnerCurrentAccountId.Value,
                Debit: creditPartner ? 0m : amount,
                Credit: creditPartner ? amount : 0m,
                SystemCurrency.BaseCurrencyCode,
                amount,
                1m,
                AccountingPartyType.Partner,
                share.PartnerId,
                ContractId: contractId,
                Description: "Partner share of contract profit"));
        }

        var allocationDate = await ResolveAllocationDateAsync(contractId, cancellationToken);
        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForProfitAllocation(companyId, contractId),
            allocationDate,
            allocationDate,
            allocationDate,
            SourceModule,
            lines,
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: contractId,
            Description: $"Profit allocation for contract #{contractId}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(contractId, companyId, profitUsd, PaymentPostingStatus.Posted, null);
            return new ProfitAllocationResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Partnership profit allocation failed for contract {ContractId}", contractId);
            throw;
        }
    }

    /// <summary>
    /// Profit is earned when the goods are sold, so the appropriation is dated on the contract's
    /// last sale rather than on whatever day the backfill happens to run.
    /// </summary>
    private async Task<DateTime> ResolveAllocationDateAsync(
        int contractId,
        CancellationToken cancellationToken)
    {
        var lastSale = await db.SalesTransactions
            .AsNoTracking()
            .Where(x => !x.IsCancelled
                && (x.ContractId == contractId || x.SourcePurchaseContractId == contractId))
            .OrderByDescending(x => x.SaleDate)
            .Select(x => (DateTime?)x.SaleDate)
            .FirstOrDefaultAsync(cancellationToken);

        return (lastSale ?? Time.AfghanistanBusinessClock.SystemToday).Date;
    }

    private ProfitAllocationResult Skipped(int contractId, string reason)
    {
        LogOutcome(contractId, 0, 0m, PaymentPostingStatus.Skipped, reason);
        return new ProfitAllocationResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        int contractId,
        int companyId,
        decimal profitUsd,
        PaymentPostingStatus status,
        string? reason)
        => logger.LogInformation(
            "Partnership profit allocation: ContractId {ContractId}, CompanyId {CompanyId}, "
            + "BookProfitUsd {BookProfitUsd}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            contractId, companyId, profitUsd, status, reason);
}
