using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Accounting;

/// <summary>
/// یک شریک در یک قرارداد: آنچه صورت‌حساب شراکت می‌گوید، آنچه دفتر کل ثبت کرده، و اگر فرق
/// دارند، دقیقاً چقدر و چرا.
/// </summary>
/// <param name="StatementNetPositionUsd">
/// علامتِ داخلیِ صورت‌حساب: مثبت = شریک طلبکار است.
/// </param>
/// <param name="LedgerPartnerCurrentUsd">
/// ماندهٔ حساب جاری شرکا برای همین شریک و همین قرارداد، به قاعدهٔ حسابداری (بستانکار − بدهکار)
/// تا با علامتِ صورت‌حساب هم‌جهت شود.
/// </param>
/// <param name="CompanyFundedContributionUsd">
/// آنچه از صندوق شرکت برای این قرارداد پرداخت شده و صورت‌حساب آن را آوردهٔ مالکِ شرکت می‌داند.
/// این مبلغ در دفتر کل به‌عنوان کاهش نقد و تسویهٔ بدهی ثبت شده، نه به‌عنوان آوردهٔ شریک.
/// </param>
public sealed record PartnerCurrentReconciliationRow(
    int ContractId,
    string ContractNumber,
    int PartnerId,
    string PartnerName,
    decimal CompanyFundedContributionUsd,
    decimal PartnerFundedContributionUsd,
    decimal SaleProceedsHeldUsd,
    decimal SettlementsNetUsd,
    decimal ProfitShareUsd,
    decimal StatementNetPositionUsd,
    decimal LedgerPartnerCurrentUsd,
    decimal DifferenceUsd,
    string? DifferenceReason)
{
    public bool IsReconciled => DifferenceUsd == 0m;
}

public sealed record PartnerCurrentReconciliationReport(
    IReadOnlyList<PartnerCurrentReconciliationRow> Rows)
{
    public bool IsFullyReconciled => Rows.All(x => x.IsReconciled);
}

public interface IPartnerCurrentReconciliationService
{
    Task<PartnerCurrentReconciliationReport> BuildAsync(
        int? contractId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// می‌گوید حساب جاری شرکا در دفتر کل با صورت‌حساب شراکت می‌خواند یا نه — و وقتی نمی‌خواند،
/// اختلاف را با نامش گزارش می‌کند به‌جای اینکه با یک سند مصنوعی صفرش کند.
///
/// چرا اختلاف اصلاً وجود دارد: دو نوع آورده هست و فقط یکی‌شان رویدادِ دفتر کل است.
///
///   • آوردهٔ شریک از جیب خودش — صندوق شرکت حرکت نکرده. دفتر کل حساب جاری شریک را بستانکار
///     می‌کند و همان عدد در صورت‌حساب هم هست. کاملاً منطبق.
///
///   • پرداخت از صندوق شرکت برای قرارداد شراکتی — صورت‌حساب آن را آوردهٔ مالکِ شرکت می‌شمارد،
///     ولی در دفتر کل همان پول واقعاً از صندوق رفته و بدهی را بسته است: <c>Dr AP / Cr Cash</c>.
///     برای اینکه این آورده در حساب جاری شریک هم بنشیند، باید ورودِ همان سرمایه به صندوق قبلاً
///     ثبت شده باشد (<c>Dr Cash / Cr Partner Current</c>). چنین رویدادی در دادهٔ عملیاتی این
///     سیستم وجود ندارد و ساختنش یعنی جعلِ یک تراکنش نقدیِ رخ‌نداده.
///
/// پس حساب جاری شرکا فقط جریان‌های واقعیِ شریک را نگه می‌دارد و این سرویس، سهمِ Company-funded
/// را جداگانه و با نام گزارش می‌کند. تطبیق یعنی:
///
///   <c>StatementNetPosition = LedgerPartnerCurrent + CompanyFundedContribution</c>
///
/// و <see cref="PartnerCurrentReconciliationRow.DifferenceUsd"/> باقیماندهٔ همین معادله است —
/// صفر بودنش یعنی هیچ‌چیزِ توضیح‌نداده‌ای نمانده.
/// </summary>
public sealed class PartnerCurrentReconciliationService(
    ApplicationDbContext db,
    IPartnershipStatementService statements) : IPartnerCurrentReconciliationService
{
    public async Task<PartnerCurrentReconciliationReport> BuildAsync(
        int? contractId = null,
        CancellationToken cancellationToken = default)
    {
        var contracts = await db.Contracts
            .AsNoTracking()
            .Where(x => x.OwnershipType == ContractOwnershipType.Partnership)
            .Where(x => contractId == null || x.Id == contractId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ContractNumber, x.CompanyId })
            .ToListAsync(cancellationToken);

        var rows = new List<PartnerCurrentReconciliationRow>();
        foreach (var contract in contracts)
        {
            var statement = await statements.BuildForContractAsync(contract.Id, cancellationToken);
            if (statement is null)
                continue;

            var ledgerByPartner = await LoadPartnerCurrentBalancesAsync(
                contract.CompanyId, contract.Id, cancellationToken);

            foreach (var partner in statement.Partners)
            {
                // «آوردهٔ شرکتی» = بخشی از FundingUsd که از صندوق شرکت رفته. FundingUsd هر دو
                // نوع را با هم دارد، پس سهمِ واقعاً شخصیِ شریک از خودِ پرداخت‌ها خوانده می‌شود.
                var partnerFunded = await LoadPartnerFundedContributionAsync(
                    contract.Id, partner.PartnerId, cancellationToken);
                var companyFunded = partner.FundingUsd - partnerFunded;

                var ledger = ledgerByPartner.GetValueOrDefault(partner.PartnerId);
                var difference = PartnerProfitAllocationPolicy.Round(
                    partner.NetPositionUsd - ledger - companyFunded);

                rows.Add(new PartnerCurrentReconciliationRow(
                    ContractId: contract.Id,
                    ContractNumber: contract.ContractNumber,
                    PartnerId: partner.PartnerId,
                    PartnerName: partner.PartnerName,
                    CompanyFundedContributionUsd: PartnerProfitAllocationPolicy.Round(companyFunded),
                    PartnerFundedContributionUsd: PartnerProfitAllocationPolicy.Round(partnerFunded),
                    SaleProceedsHeldUsd: partner.ProceedsHeldUsd,
                    SettlementsNetUsd: PartnerProfitAllocationPolicy.Round(
                        partner.SettlementsPaidUsd - partner.SettlementsReceivedUsd),
                    ProfitShareUsd: partner.ProfitShareUsd,
                    StatementNetPositionUsd: partner.NetPositionUsd,
                    LedgerPartnerCurrentUsd: ledger,
                    DifferenceUsd: difference,
                    DifferenceReason: companyFunded == 0m
                        ? (difference == 0m ? null : "UNEXPLAINED")
                        : "COMPANY_FUNDED_CONTRIBUTION_NOT_IN_LEDGER"));
            }
        }

        return new PartnerCurrentReconciliationReport(rows);
    }

    /// <summary>
    /// ماندهٔ حساب جاری شرکا به تفکیک شریک، فقط از سطرهای همین قرارداد. علامت به قاعدهٔ
    /// صورت‌حساب برگردانده می‌شود (بستانکار − بدهکار = شریک طلبکار است).
    /// </summary>
    private async Task<Dictionary<int, decimal>> LoadPartnerCurrentBalancesAsync(
        int companyId,
        int contractId,
        CancellationToken cancellationToken)
    {
        var partnerCurrentAccountId = await db.AccountingSettings
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId)
            .Select(x => x.PartnerCurrentAccountId)
            .SingleOrDefaultAsync(cancellationToken);
        if (partnerCurrentAccountId is null or <= 0)
            return [];

        var lines = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == partnerCurrentAccountId.Value
                && x.PartyType == AccountingPartyType.Partner
                && x.PartyId != null
                && x.ContractId == contractId
                && x.JournalEntry!.Status == JournalEntryStatus.Posted)
            .GroupBy(x => x.PartyId!.Value)
            .Select(g => new { PartnerId = g.Key, Net = g.Sum(x => x.Credit) - g.Sum(x => x.Debit) })
            .ToListAsync(cancellationToken);

        return lines.ToDictionary(
            x => x.PartnerId,
            x => PartnerProfitAllocationPolicy.Round(x.Net));
    }

    /// <summary>
    /// آنچه شریک واقعاً از جیب خودش داده، منهای آنچه از پول قرارداد نزد خودش نگه داشته —
    /// همان علامتی که صورت‌حساب برای FundingUsd به‌کار می‌برد، ولی فقط برای پرداخت‌هایی که
    /// خودِ سطرشان می‌گوید منبعشان شریک بوده، نه صندوق شرکت.
    /// </summary>
    private async Task<decimal> LoadPartnerFundedContributionAsync(
        int contractId,
        int partnerId,
        CancellationToken cancellationToken)
    {
        var rows = await db.PaymentTransactions
            .AsNoTracking()
            .Where(x => x.ContractId == contractId
                && x.FundingSource == PaymentFundingSource.Partner
                && x.PaidByPartnerId == partnerId)
            .Select(x => new { x.Direction, x.AmountUsd })
            .ToListAsync(cancellationToken);

        return PartnerProfitAllocationPolicy.Round(
            rows.Sum(x => x.Direction == PaymentDirection.Out ? x.AmountUsd : -x.AmountUsd));
    }
}
