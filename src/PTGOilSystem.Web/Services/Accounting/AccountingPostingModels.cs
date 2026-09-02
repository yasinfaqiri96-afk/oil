using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record AccountingPostLine(
    int AccountId,
    decimal Debit,
    decimal Credit,
    string TransactionCurrencyCode,
    decimal TransactionAmount,
    decimal ExchangeRate,
    AccountingPartyType? PartyType = null,
    int? PartyId = null,
    int? ContractId = null,
    int? ShipmentId = null,
    int? TankId = null,
    int? ProductId = null,
    int? CashAccountId = null,
    string? Description = null,
    int? OperationalAssetId = null);

public sealed record AccountingPostRequest(
    int CompanyId,
    string JournalNumber,
    DateTime AccountingDate,
    DateTime DocumentDate,
    DateTime OperationDate,
    string SourceModule,
    IReadOnlyCollection<AccountingPostLine> Lines,
    string? SourceEventId = null,
    string? SourceEntityType = null,
    int? SourceEntityId = null,
    string? Description = null,
    bool IsOpening = false,
    bool IsClosing = false,
    bool IsAdjustment = false,
    int? PostedByUserId = null);

public sealed record AccountingReversalRequest(
    int JournalEntryId,
    string JournalNumber,
    DateTime AccountingDate,
    string SourceModule,
    string SourceEventId,
    string? Description = null,
    int? PostedByUserId = null);
