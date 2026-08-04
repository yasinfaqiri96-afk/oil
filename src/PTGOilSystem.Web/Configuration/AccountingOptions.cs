namespace PTGOilSystem.Web.Configuration;

public sealed class AccountingOptions
{
    public const string SectionName = "Accounting";

    // Stage 2 is infrastructure-only. Operational posting remains opt-in.
    public bool Enabled { get; set; }
    public string DefaultFunctionalCurrencyCode { get; set; } = "USD";
    public AccountingPilotOptions Pilots { get; set; } = new();
}

public sealed class AccountingPilotOptions
{
    public bool ContractBalanceTransfer { get; set; }
    public bool SupplierPaymentAllocation { get; set; }

    // انتقال «مانده قابل انتقال» تأمین‌کننده. هنوز Adapter ژورنال ندارد؛ روشن‌کردن این فلگ
    // عمداً ثبت انتقال را متوقف می‌کند تا سطر Legacy بدون سند ژورنال ثبت نشود.
    public bool SupplierBalanceTransfer { get; set; }

    // Stage 4 — receipts and payments. Each cash sub-module owns an independent flag so a
    // single mapping can be piloted without exposing the others.
    public bool CustomerReceipt { get; set; }
    public bool CustomerAdvance { get; set; }
    public bool SupplierPayment { get; set; }
    public bool SupplierPrepayment { get; set; }
    public bool SarrafPayment { get; set; }

    // Stage 5 — expenses, freight, commission. Expense accrues the liability; the two
    // payment flags settle it, so they are normally enabled together with Expense.
    public bool Expense { get; set; }
    public bool ExpensePayment { get; set; }
    public bool CommissionPayment { get; set; }

    // Stage 6 — purchase and inventory. Purchase raises the supplier payable against goods in
    // transit; InventoryReceipt moves those goods from in-transit into inventory, so the two
    // are normally enabled together.
    public bool Purchase { get; set; }
    public bool InventoryReceipt { get; set; }

    // Stage 7 — sales and cost of goods sold. Sale posts the revenue; Cogs values what left
    // inventory. Cogs depends on InventoryReceipt having filled the valuation pool.
    public bool Sale { get; set; }
    public bool Cogs { get; set; }

    // Stage 8 — losses, shortage, sarraf. InventoryLoss writes off stock the tanks lost and,
    // like Cogs, depends on InventoryReceipt having filled the valuation pool. ShortageCharge
    // recognises what a carrier owes for what did not arrive. The two sarraf flags cover the
    // settlement flows that the Stage 4 SarrafPayment flag does not reach.
    public bool InventoryLoss { get; set; }
    public bool ShortageCharge { get; set; }
    public bool SarrafSettlement { get; set; }
    public bool ThreeWaySettlement { get; set; }

    // Inter-terminal transfers. Goods at different terminals cost different amounts to have got
    // there, so the valuation pool is keyed by terminal — which means a transfer has to carry its
    // cost from one pool to the other, or the sale at the destination values against the wrong
    // stock. Without this flag a transfer moves tonnes and no money, and Cogs at the destination
    // is unsafe; that is why enabling Cogs depends on enabling this.
    public bool InventoryTransfer { get; set; }
}
