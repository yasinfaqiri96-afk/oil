using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public class JournalEntryLine : BaseEntity
{
    public int JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int LineNumber { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public AccountingPartyType? PartyType { get; set; }
    public int? PartyId { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int? TankId { get; set; }
    public StorageTank? Tank { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    [Required, MaxLength(10)]
    public string TransactionCurrencyCode { get; set; } = "USD";

    public decimal TransactionAmount { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;

    [MaxLength(1000)]
    public string? Description { get; set; }
}
