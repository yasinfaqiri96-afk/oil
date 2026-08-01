using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Reconciliation;

/// <summary>
/// تمام منطق مغایرت‌گیری در همین سرویس زندگی می‌کند؛ <c>ReconciliationController</c> فقط
/// فیلتر می‌گیرد، دسترسی را بررسی می‌کند و نتیجه را نمایش یا Export می‌دهد.
/// همهٔ queryها read-only و <c>AsNoTracking</c> هستند و هیچ رکوردی نوشته یا اصلاح نمی‌شود.
/// </summary>
public partial class ReconciliationService : IReconciliationService
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IAfghanistanBusinessClock _businessClock;

    public ReconciliationService(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IAfghanistanBusinessClock? businessClock = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _businessClock = businessClock ?? new AfghanistanBusinessClock(TimeProvider.System);
    }

    // شمارنده‌های صفحهٔ Index. هر badge به منطق کامل مغایرت نیاز دارد (نه یک COUNT ساده)،
    // بنابراین کنترلر آن را با AJAX و بعد از رندر صفحه صدا می‌زند. منطق شمارش تغییر نکرده.
    public async Task<ReconciliationSummaryCounts> BuildSummaryCountsAsync()
    {
        var openContracts = await BuildOpenContractsAsync();
        var openShipments = await BuildOpenShipmentsAsync();
        var missingLedger = await BuildMissingLedgerAsync();
        var balances = await BuildNonZeroBalancesAsync();
        var incompleteAfterReceipt = await BuildIncompleteAfterReceiptAsync();
        var employeeIssues = await BuildEmployeeReconciliationAsync();
        var roznamchaIssues = await BuildRoznamchaReconciliationAsync();
        var suspenseMoney = await BuildSuspenseMoneyAsync();
        var lossEventsCount = await _db.LossEvents.AsNoTracking().CountAsync();

        var openContractsCount = openContracts.Rows.Count;
        var openShipmentsCount = openShipments.ShipmentsWithoutSales.Count
            + openShipments.ShipmentsWithoutExpenses.Count
            + openShipments.DispatchesWithoutReceipt.Count;
        var missingLedgerCount = missingLedger.SalesWithoutLedger.Count
            + missingLedger.ExpensesWithoutLedger.Count
            + missingLedger.PaymentsWithoutLedger.Count
            + missingLedger.DirectSaleIssueCount
            + missingLedger.DirectDispatchIssueCount
            + missingLedger.InventoryIntegrityIssueCount
            + missingLedger.SupplierPaymentIssueCount
            + missingLedger.ServiceProviderIssueCount
            + missingLedger.SarrafSettlementIssueCount
            + missingLedger.OperationalAssetIssueCount;
        var nonZeroBalancesCount = balances.ContractBalances.Count
            + balances.CustomerBalances.Count
            + balances.SupplierBalances.Count;

        return new ReconciliationSummaryCounts
        {
            OpenContracts = openContractsCount,
            OpenShipments = openShipmentsCount,
            IncompleteAfterReceipt = incompleteAfterReceipt.TotalCount,
            MissingLedger = missingLedgerCount,
            NonZeroBalances = nonZeroBalancesCount,
            LossEvents = lossEventsCount,
            EmployeeIssues = employeeIssues.TotalCount,
            RoznamchaIssues = roznamchaIssues.TotalCount,
            SuspenseMoney = suspenseMoney.TotalCount
        };
    }

    public async Task<RoznamchaReconciliationViewModel> BuildRoznamchaReconciliationAsync()
    {
        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Customer)
            .Include(p => p.Supplier)
            .Include(p => p.ServiceProvider)
            .Include(p => p.Sarraf)
            .Include(p => p.Employee)
            .Include(p => p.Driver)
            .Include(p => p.ExpenseTransaction)
            .Include(p => p.LedgerEntry)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        var cashAccountIds = payments
            .Select(p => p.CashAccountId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var cashAccounts = cashAccountIds.Count == 0
            ? new Dictionary<int, CashAccount>()
            : await _db.CashAccounts
                .AsNoTracking()
                .Where(a => cashAccountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id);

        foreach (var payment in payments)
        {
            if (cashAccounts.TryGetValue(payment.CashAccountId, out var cashAccount))
            {
                payment.CashAccount = cashAccount;
            }
        }

        var expenseIds = payments
            .Where(p => p.ExpenseTransactionId.HasValue)
            .Select(p => p.ExpenseTransactionId!.Value)
            .Distinct()
            .ToList();
        var expenseLedgerIds = expenseIds.Count == 0
            ? new HashSet<int>()
            : (await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
                .Select(l => l.SourceId)
                .ToListAsync())
                .ToHashSet();

        var paymentsWithoutCounterparty = payments
            .Where(p =>
                (p.PaymentKind == PaymentKind.TruckPayment && !p.DriverId.HasValue && !p.TruckDispatchId.HasValue)
                || (p.PaymentKind == PaymentKind.ServiceProviderPayment && !p.ServiceProviderId.HasValue)
                || (p.PaymentKind == PaymentKind.SarrafSettlement && !p.SarrafId.HasValue)
                || (p.PaymentKind == PaymentKind.ExpensePayment
                    && !p.ExpenseTransactionId.HasValue
                    && !p.ContractId.HasValue
                    && !p.ShipmentId.HasValue
                    && !p.TruckDispatchId.HasValue))
            .Select(p => ToRoznamchaIssue(p, "Roznamcha payment has a counterparty type but no usable related ID."))
            .ToList();

        var paymentsWithoutCashAccount = payments
            .Where(p => p.CashAccountId <= 0 || p.CashAccount is null)
            .Select(p => ToRoznamchaIssue(p, "Roznamcha payment has no valid cash/bank account."))
            .ToList();

        var ledgerAmountMismatches = payments
            .Where(p => p.LedgerEntry is not null
                && (decimal.Round(p.LedgerEntry.AmountUsd, 4, MidpointRounding.AwayFromZero)
                    != decimal.Round(p.AmountUsd, 4, MidpointRounding.AwayFromZero)
                    || p.LedgerEntry.SourceId != p.Id
                    || p.LedgerEntry.SourceType != p.PaymentKind.ToString()))
            .Select(p => ToRoznamchaIssue(p, "Roznamcha payment amount/source does not match linked ledger."))
            .ToList();

        var expenseDoubleCountingRisks = payments
            .Where(p => p.PaymentKind == PaymentKind.ExpensePayment
                && p.ExpenseTransactionId.HasValue
                && p.LedgerEntryId.HasValue
                && expenseLedgerIds.Contains(p.ExpenseTransactionId.Value))
            .Select(p => ToRoznamchaIssue(p, "Expense payment is linked to an ExpenseTransaction that already has an Expense ledger. Review P&L double counting risk.", "Warning"))
            .ToList();

        var employeePaymentsWithoutEmployeeLink = payments
            .Where(p => p.PaymentKind is PaymentKind.EmployeeSalaryPayment or PaymentKind.EmployeeSalaryAdvance or PaymentKind.EmployeeReturn
                && !p.EmployeeId.HasValue)
            .Select(p => ToRoznamchaIssue(p, "Employee payment/return has no EmployeeId."))
            .ToList();

        var supplierCustomerPaymentsWithoutProfileLink = payments
            .Where(p =>
                (p.PaymentKind is PaymentKind.SupplierPayment or PaymentKind.SupplierReceipt && !p.SupplierId.HasValue)
                || (p.PaymentKind is PaymentKind.CustomerReceipt or PaymentKind.CustomerPayment && !p.CustomerId.HasValue)
                || (p.PaymentKind == PaymentKind.SarrafSettlement && !p.SarrafId.HasValue))
            .Select(p => ToRoznamchaIssue(p, "Supplier/customer payment has no profile link."))
            .ToList();

        return new RoznamchaReconciliationViewModel
        {
            PaymentsWithoutCounterparty = paymentsWithoutCounterparty,
            PaymentsWithoutCashAccount = paymentsWithoutCashAccount,
            LedgerAmountMismatches = ledgerAmountMismatches,
            ExpenseDoubleCountingRisks = expenseDoubleCountingRisks,
            EmployeePaymentsWithoutEmployeeLink = employeePaymentsWithoutEmployeeLink,
            SupplierCustomerPaymentsWithoutProfileLink = supplierCustomerPaymentsWithoutProfileLink
        };
    }

    private static RoznamchaReconciliationItemViewModel ToRoznamchaIssue(
        PaymentTransaction payment,
        string issue,
        string status = "Needs Review")
        => new()
        {
            PaymentTransactionId = payment.Id,
            PaymentDate = payment.PaymentDate,
            DirectionName = PaymentDirectionLabels.ToPersian(payment.Direction),
            PaymentKindName = PaymentKindLabels.ToPersian(payment.PaymentKind),
            CashAccountName = payment.CashAccount?.Name,
            CounterpartyName = payment.Customer?.Name
                ?? payment.Supplier?.Name
                ?? payment.ServiceProvider?.Name
                ?? payment.Sarraf?.Name
                ?? payment.Employee?.FullName
                ?? payment.Driver?.FullName
                ?? payment.ExpenseTransaction?.Description,
            AmountUsd = payment.AmountUsd,
            LedgerEntryId = payment.LedgerEntryId,
            LedgerAmountUsd = payment.LedgerEntry?.AmountUsd,
            Reference = payment.Reference,
            Issue = issue,
            Status = status
        };

    public async Task<EmployeeReconciliationViewModel> BuildEmployeeReconciliationAsync()
    {
        var cashTypes = new[]
        {
            EmployeeSalaryTransactionType.SalaryPayment,
            EmployeeSalaryTransactionType.SalaryAdvance
        };

        var transactions = await _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Include(t => t.Employee)
            .Include(t => t.CashAccount)
            .Include(t => t.PaymentTransaction)
            .Include(t => t.LedgerEntry)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id)
            .ToListAsync();

        var transactionsWithoutLedger = transactions
            .Where(t => !t.IsCancelled
                && cashTypes.Contains(t.TransactionType)
                && (!t.LedgerEntryId.HasValue || t.LedgerEntry is null))
            .Select(t => ToEmployeeSalaryIssue(t, "Employee salary cash transaction has no ledger entry."))
            .ToList();

        var transactionsWithoutCashAccount = transactions
            .Where(t => !t.IsCancelled
                && cashTypes.Contains(t.TransactionType)
                && !t.CashAccountId.HasValue)
            .Select(t => ToEmployeeSalaryIssue(t, "Employee salary payment/advance has no cash account."))
            .ToList();

        var ledgerAmountMismatches = transactions
            .Where(t => !t.IsCancelled
                && t.LedgerEntry is not null
                && (decimal.Round(t.LedgerEntry.AmountUsd, 4, MidpointRounding.AwayFromZero)
                    != decimal.Round(t.AmountUsd, 4, MidpointRounding.AwayFromZero)
                    || (t.PaymentTransaction is not null
                        && decimal.Round(t.PaymentTransaction.AmountUsd, 4, MidpointRounding.AwayFromZero)
                            != decimal.Round(t.AmountUsd, 4, MidpointRounding.AwayFromZero))))
            .Select(t => ToEmployeeSalaryIssue(t, "Employee salary transaction amount does not match ledger/payment."))
            .ToList();

        var cancelledWithActiveLedger = transactions
            .Where(t => t.IsCancelled && (t.LedgerEntryId.HasValue || t.PaymentTransactionId.HasValue))
            .Select(t => ToEmployeeSalaryIssue(t, "Cancelled employee salary transaction still has active payment/ledger trace.", "Warning"))
            .ToList();

        var negativeOrUnexpectedBalances = transactions
            .GroupBy(t => t.EmployeeId)
            .Select(g =>
            {
                var employee = g.First().Employee;
                var summary = EmployeeSalarySummaryCalculator.FromTransactions(g);
                return new
                {
                    EmployeeId = g.Key,
                    EmployeeCode = employee?.EmployeeCode ?? "",
                    EmployeeName = employee?.FullName ?? "",
                    Balance = summary.BalanceUsd
                };
            })
            .Where(x => x.Balance < 0m)
            .Select(x => new EmployeeBalanceReconciliationItemViewModel
            {
                EmployeeId = x.EmployeeId,
                EmployeeCode = x.EmployeeCode,
                EmployeeName = x.EmployeeName,
                BalanceUsd = x.Balance,
                Issue = "Employee balance is negative; review advances/payments against accrued salary."
            })
            .ToList();

        return new EmployeeReconciliationViewModel
        {
            TransactionsWithoutLedger = transactionsWithoutLedger,
            TransactionsWithoutCashAccount = transactionsWithoutCashAccount,
            LedgerAmountMismatches = ledgerAmountMismatches,
            CancelledTransactionsWithActiveLedger = cancelledWithActiveLedger,
            NegativeOrUnexpectedBalances = negativeOrUnexpectedBalances
        };
    }

    public async Task<OpenContractsViewModel> BuildOpenContractsAsync()
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .OrderBy(c => c.ContractNumber)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                ProductName = c.Product != null ? c.Product.Name : "",
                ContractUnitText = c.Unit != null
                    ? c.Unit.Symbol ?? c.Unit.Code ?? c.Unit.NamePersian ?? c.Unit.Name ?? "—"
                    : "—",
                c.QuantityMt
            })
            .ToListAsync();

        var loadedByContract = await _purchaseAggregation.GetLoadedQuantityByContractAsync();
        var receivedByContract = await _db.LoadingReceipts
            .AsNoTracking()
            .Include(r => r.LoadingRegister)
            .Where(r => r.LoadingRegister != null)
            .GroupBy(r => r.LoadingRegister!.ContractId)
            .Select(g => new { ContractId = g.Key, Quantity = g.Sum(r => r.ReceivedQuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.Quantity);
        var soldByContract = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ContractId.HasValue)
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new { ContractId = g.Key, Quantity = g.Sum(s => s.QuantityMt) })
            .ToDictionaryAsync(x => x.ContractId, x => x.Quantity);

        var rows = contracts
            .Select(c =>
            {
                var loaded = loadedByContract.GetValueOrDefault(c.Id);
                var received = receivedByContract.GetValueOrDefault(c.Id);
                var sold = soldByContract.GetValueOrDefault(c.Id);
                var consumed = Math.Max(loaded, Math.Max(received, sold));
                return new OpenContractItemViewModel
                {
                    ContractId = c.Id,
                    ContractNumber = c.ContractNumber,
                    ProductName = c.ProductName,
                    ContractUnitText = c.ContractUnitText,
                    ContractQuantityMt = c.QuantityMt,
                    LoadedQuantityMt = loaded,
                    ReceivedQuantityMt = received,
                    SoldQuantityMt = sold,
                    RemainingQuantityMt = c.QuantityMt - consumed,
                    Status = c.QuantityMt - consumed > 0m ? "Open" : "OK"
                };
            })
            .Where(r => r.RemainingQuantityMt > 0m)
            .OrderByDescending(r => r.RemainingQuantityMt)
            .ToList();

        return new OpenContractsViewModel { Rows = rows };
    }

    public async Task<OpenShipmentsViewModel> BuildOpenShipmentsAsync()
    {
        var shipmentsWithoutSales = await _db.Shipments
            .AsNoTracking()
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Where(s => !_db.SalesTransactions.Any(t => t.ShipmentId == s.Id))
            .OrderBy(s => s.ShipmentCode)
            .Select(s => new OpenShipmentItemViewModel
            {
                ShipmentId = s.Id,
                ShipmentCode = s.ShipmentCode,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                ContractUnitText = s.Contract != null
                    ? s.Contract.Unit != null
                        ? s.Contract.Unit.Symbol ?? s.Contract.Unit.Code ?? s.Contract.Unit.NamePersian ?? s.Contract.Unit.Name ?? "—"
                        : "—"
                    : "—",
                QuantityMt = s.QuantityMt,
                Status = "Open"
            })
            .ToListAsync();

        var shipmentsWithoutExpenses = await _db.Shipments
            .AsNoTracking()
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Where(s => !_db.ExpenseTransactions.Any(e => e.ShipmentId == s.Id))
            .OrderBy(s => s.ShipmentCode)
            .Select(s => new OpenShipmentItemViewModel
            {
                ShipmentId = s.Id,
                ShipmentCode = s.ShipmentCode,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                ContractUnitText = s.Contract != null
                    ? s.Contract.Unit != null
                        ? s.Contract.Unit.Symbol ?? s.Contract.Unit.Code ?? s.Contract.Unit.NamePersian ?? s.Contract.Unit.Name ?? "—"
                        : "—"
                    : "—",
                QuantityMt = s.QuantityMt,
                Status = "Open"
            })
            .ToListAsync();

        var dispatchesWithoutReceipt = await _db.TruckDispatches
            .AsNoTracking()
            .Include(d => d.Truck)
            .Include(d => d.Contract)
                .ThenInclude(c => c!.Unit)
            .Where(d => !_db.DeliveryReceipts.Any(r => r.TruckDispatchId == d.Id))
            .OrderByDescending(d => d.DispatchDate)
            .Select(d => new DispatchNeedsReviewItemViewModel
            {
                DispatchId = d.Id,
                DispatchDate = d.DispatchDate,
                TruckPlateNumber = d.Truck != null ? d.Truck.PlateNumber : "",
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : "",
                ContractUnitText = d.Contract != null
                    ? d.Contract.Unit != null
                        ? d.Contract.Unit.Symbol ?? d.Contract.Unit.Code ?? d.Contract.Unit.NamePersian ?? d.Contract.Unit.Name ?? "—"
                        : "—"
                    : "—",
                LoadedQuantityMt = d.LoadedQuantityMt,
                Status = "Needs Review",
                Reason = "برای این dispatch هیچ DeliveryReceipt ثبت نشده است؛ settlement جداگانه هنوز workflow قطعی ندارد."
            })
            .ToListAsync();

        return new OpenShipmentsViewModel
        {
            ShipmentsWithoutSales = shipmentsWithoutSales,
            ShipmentsWithoutExpenses = shipmentsWithoutExpenses,
            DispatchesWithoutReceipt = dispatchesWithoutReceipt
        };
    }

    public async Task<MissingLedgerViewModel> BuildMissingLedgerAsync()
    {
        var paymentSourceTypes = Enum.GetNames<PaymentKind>();

        var directSaleAllocations = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.SalesTransaction)
                .ThenInclude(s => s!.Customer)
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectSale)
            .OrderBy(a => a.Id)
            .ToListAsync();
        var directSaleSaleIds = directSaleAllocations
            .Where(a => a.SalesTransactionId.HasValue)
            .Select(a => a.SalesTransactionId!.Value)
            .Distinct()
            .ToList();

        var directDispatchAllocations = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.LoadingReceipt)
            .Include(a => a.DestinationLocation)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.Truck)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.Driver)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.DestinationLocation)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.SalesTransaction)
            .AsSplitQuery()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck)
            .OrderBy(a => a.Id)
            .ToListAsync();
        var directDispatchAllocationById = directDispatchAllocations.ToDictionary(a => a.Id);
        var directFromReceiptDispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Include(d => d.Contract)
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.DestinationLocation)
            .Include(d => d.SalesTransaction)
                .ThenInclude(s => s!.Customer)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.SourcePurchaseContract)
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled)
            .OrderBy(d => d.Id)
            .ToListAsync();
        var directDispatchIds = directFromReceiptDispatches
            .Select(d => d.Id)
            .Distinct()
            .ToList();
        var directDispatchReferenceMap = directDispatchIds.ToDictionary(id => $"TRUCK-DISPATCH:{id}", id => id);
        var directDispatchReferences = directDispatchReferenceMap.Keys.ToList();
        var directDispatchMovementRows = directDispatchIds.Count == 0
            ? []
            : await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.ReferenceDocument != null && directDispatchReferences.Contains(m.ReferenceDocument))
                .Select(m => m.ReferenceDocument!)
                .ToListAsync();
        var directDispatchIdsWithInventoryMovement = directDispatchMovementRows
            .Select(reference => directDispatchReferenceMap.GetValueOrDefault(reference))
            .Where(id => id > 0)
            .ToHashSet();
        var directDispatchSaleIds = directFromReceiptDispatches
            .Where(d => d.SalesTransactionId.HasValue)
            .Select(d => d.SalesTransactionId!.Value)
            .Distinct()
            .ToList();
        // یک بار خواندنِ سطرهای دفتر کلِ «Sale» برای هر دو مسیر (فروش مستقیم و ارسال مستقیم)
        // به‌جای دو query جدا؛ فقط ستون‌های لازم خوانده می‌شود، نه کل رکورد.
        var saleLedgerLookupIds = directSaleSaleIds
            .Concat(directDispatchSaleIds)
            .Distinct()
            .ToList();
        var saleLedgerRows = saleLedgerLookupIds.Count == 0
            ? []
            : await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Sale" && saleLedgerLookupIds.Contains(l.SourceId))
                .Select(l => new { l.SourceId, l.Side, l.AmountUsd })
                .ToListAsync();

        var directSaleSaleIdSet = directSaleSaleIds.ToHashSet();
        var directDispatchSaleIdSet = directDispatchSaleIds.ToHashSet();
        var directSaleLedgerAmountBySaleId = saleLedgerRows
            .Where(l => directSaleSaleIdSet.Contains(l.SourceId))
            .GroupBy(l => l.SourceId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));
        var directDispatchLedgerAmountBySaleId = saleLedgerRows
            .Where(l => directDispatchSaleIdSet.Contains(l.SourceId))
            .GroupBy(l => l.SourceId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd));

        var sales = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !directSaleSaleIds.Contains(s.Id)
                && !directDispatchSaleIds.Contains(s.Id)
                && !_db.LedgerEntries.Any(l => l.SourceType == "Sale" && l.SourceId == s.Id))
            .OrderByDescending(s => s.SaleDate)
            .Select(s => new MissingLedgerItemViewModel
            {
                SourceId = s.Id,
                SourceType = "Sale",
                Date = s.SaleDate,
                Reference = s.InvoiceNumber,
                AmountUsd = s.TotalUsd,
                Status = "Missing Ledger"
            })
            .ToListAsync();

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !_db.LedgerEntries.Any(l => l.SourceType == "Expense" && l.SourceId == e.Id))
            .OrderByDescending(e => e.ExpenseDate)
            .Select(e => new MissingLedgerItemViewModel
            {
                SourceId = e.Id,
                SourceType = "Expense",
                Date = e.ExpenseDate,
                Reference = e.Description ?? ("Expense #" + e.Id),
                AmountUsd = e.AmountUsd,
                Status = "Missing Ledger"
            })
            .ToListAsync();

        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => !_db.LedgerEntries.Any(l => paymentSourceTypes.Contains(l.SourceType) && l.SourceId == p.Id))
            .OrderByDescending(p => p.PaymentDate)
            .Select(p => new MissingLedgerItemViewModel
            {
                SourceId = p.Id,
                SourceType = p.PaymentKind.ToString(),
                Date = p.PaymentDate,
                Reference = p.Reference ?? ("Payment #" + p.Id),
                AmountUsd = p.AmountUsd,
                Status = "Missing Ledger"
            })
            .ToListAsync();

        var supplierPaymentRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(p => p.LedgerEntry)
            .Where(p => p.PaymentKind == PaymentKind.SupplierPayment)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        SupplierPaymentReconciliationItemViewModel ToSupplierPaymentIssue(PaymentTransaction payment, string issue)
            => new()
            {
                PaymentId = payment.Id,
                PaymentDate = payment.PaymentDate,
                SupplierId = payment.SupplierId,
                SupplierName = payment.Supplier?.Name ?? payment.Contract?.Supplier?.Name ?? "—",
                ContractId = payment.ContractId,
                ContractNumber = payment.Contract?.ContractNumber ?? "—",
                Reference = payment.Reference ?? ("Payment #" + payment.Id),
                AmountUsd = payment.AmountUsd,
                LedgerEntryId = payment.LedgerEntryId,
                Issue = issue
            };

        var supplierPaymentsWithoutSupplier = supplierPaymentRows
            .Where(p => !p.SupplierId.HasValue)
            .Select(p => ToSupplierPaymentIssue(p, "پرداخت تأمین‌کننده شناسه تأمین‌کننده ندارد."))
            .ToList();

        var supplierPaymentContractSupplierMismatches = supplierPaymentRows
            .Where(p => p.SupplierId.HasValue
                && p.ContractId.HasValue
                && p.Contract?.SupplierId.HasValue == true
                && p.Contract.SupplierId.Value != p.SupplierId.Value)
            .Select(p => ToSupplierPaymentIssue(p, "شناسه تأمین‌کننده پرداخت با تأمین‌کننده قرارداد هم‌خوان نیست."))
            .ToList();

        var supplierPaymentLedgerMissingSupplierOrContract = supplierPaymentRows
            .Where(p => p.LedgerEntryId.HasValue
                && p.LedgerEntry is not null
                && (p.LedgerEntry.SupplierId != p.SupplierId
                    || p.LedgerEntry.ContractId != p.ContractId))
            .Select(p => ToSupplierPaymentIssue(p, "شناسه تأمین‌کننده یا قرارداد در رکورد دفتر کل با پرداخت هم‌خوان نیست."))
            .ToList();

        var supplierPaymentsWithoutLedger = supplierPaymentRows
            .Where(p => !p.LedgerEntryId.HasValue || p.LedgerEntry is null)
            .Select(p => ToSupplierPaymentIssue(p, "پرداخت تأمین‌کننده رکورد دفتر کل متناظر ندارد."))
            .ToList();

        var supplierLedgerRows = await _db.LedgerEntries
            .AsNoTracking()
            .Include(l => l.Supplier)
            .Include(l => l.Contract)
                .ThenInclude(c => c!.Supplier)
            .Where(l => l.SupplierId.HasValue
                || (l.ContractId.HasValue
                    && l.Contract != null
                    && l.Contract.ContractType == ContractType.Purchase
                    && l.Contract.SupplierId.HasValue))
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync();

        var supplierLedgerFxIssues = supplierLedgerRows
            .Select(l => new
            {
                Ledger = l,
                SourceCurrency = string.IsNullOrWhiteSpace(l.SourceCurrencyCode) ? l.Currency : l.SourceCurrencyCode!
            })
            .Where(x => !string.Equals(x.SourceCurrency, SystemCurrency.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase)
                && (!x.Ledger.AppliedFxRateToUsd.HasValue
                    || x.Ledger.AppliedFxRateToUsd.Value <= 0m
                    || x.Ledger.AmountUsd <= 0m))
            .Select(x => new SupplierLedgerFxIssueViewModel
            {
                LedgerEntryId = x.Ledger.Id,
                EntryDate = x.Ledger.EntryDate,
                SupplierName = x.Ledger.Supplier?.Name ?? x.Ledger.Contract?.Supplier?.Name ?? "—",
                ContractNumber = x.Ledger.Contract?.ContractNumber ?? "—",
                SourceType = x.Ledger.SourceType,
                Reference = x.Ledger.Reference ?? ("Ledger #" + x.Ledger.Id),
                SourceCurrencyCode = x.SourceCurrency,
                SourceAmount = x.Ledger.SourceAmount,
                AppliedFxRateToUsd = x.Ledger.AppliedFxRateToUsd,
                AmountUsd = x.Ledger.AmountUsd,
                Issue = "رکورد غیر USD دفتر کل تأمین‌کننده نرخ تبدیل یا مبلغ USD معتبر ندارد."
            })
            .ToList();

        var serviceProviderExpenseRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ServiceProvider)
            .Where(e => e.ServiceProviderId.HasValue)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

        var serviceProviderExpenseIds = serviceProviderExpenseRows
            .Select(e => e.Id)
            .Distinct()
            .ToList();
        var serviceProviderExpenseLedgerRows = serviceProviderExpenseIds.Count == 0
            ? []
            : await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.SourceType == "Expense" && serviceProviderExpenseIds.Contains(l.SourceId))
                .OrderBy(l => l.EntryDate)
                .ThenBy(l => l.Id)
                .ToListAsync();
        var serviceProviderExpenseLedgerBySource = serviceProviderExpenseLedgerRows
            .GroupBy(l => l.SourceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var serviceProviderPaymentRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.ServiceProvider)
            .Include(p => p.LedgerEntry)
            .Where(p => p.ServiceProviderId.HasValue || p.PaymentKind == PaymentKind.ServiceProviderPayment)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        ServiceProviderReconciliationItemViewModel ToServiceProviderExpenseIssue(
            ExpenseTransaction expense,
            string issue,
            LedgerEntry? ledgerEntry = null,
            decimal? ledgerAmountUsd = null,
            string status = "Needs Review")
            => new()
            {
                SourceId = expense.Id,
                SourceType = "Expense",
                Date = expense.ExpenseDate,
                ServiceProviderId = expense.ServiceProviderId,
                ServiceProviderName = expense.ServiceProvider?.Name ?? "—",
                Reference = expense.Description ?? ("Expense #" + expense.Id),
                AmountUsd = expense.AmountUsd,
                LedgerEntryId = ledgerEntry?.Id,
                LedgerAmountUsd = ledgerAmountUsd ?? ledgerEntry?.AmountUsd,
                Issue = issue,
                Status = status
            };

        ServiceProviderReconciliationItemViewModel ToServiceProviderPaymentIssue(
            PaymentTransaction payment,
            string issue,
            string status = "Needs Review")
            => new()
            {
                SourceId = payment.Id,
                SourceType = payment.PaymentKind.ToString(),
                Date = payment.PaymentDate,
                ServiceProviderId = payment.ServiceProviderId,
                ServiceProviderName = payment.ServiceProvider?.Name ?? "—",
                Reference = payment.Reference ?? ("Payment #" + payment.Id),
                AmountUsd = payment.AmountUsd,
                LedgerEntryId = payment.LedgerEntryId,
                LedgerAmountUsd = payment.LedgerEntry?.AmountUsd,
                Issue = issue,
                Status = status
            };

        ServiceProviderReconciliationItemViewModel ToServiceProviderLedgerIssue(
            LedgerEntry ledgerEntry,
            string issue,
            string status = "Needs Review")
            => new()
            {
                SourceId = ledgerEntry.SourceId,
                SourceType = ledgerEntry.SourceType,
                Date = ledgerEntry.EntryDate,
                ServiceProviderId = ledgerEntry.ServiceProviderId,
                ServiceProviderName = ledgerEntry.ServiceProvider?.Name ?? "—",
                Reference = ledgerEntry.Reference ?? ("Ledger #" + ledgerEntry.Id),
                AmountUsd = ledgerEntry.AmountUsd,
                LedgerEntryId = ledgerEntry.Id,
                LedgerAmountUsd = ledgerEntry.AmountUsd,
                Issue = issue,
                Status = status
            };

        var serviceProviderExpensesWithoutLedger = serviceProviderExpenseRows
            .Where(e => !e.IsCancelled && !serviceProviderExpenseLedgerBySource.ContainsKey(e.Id))
            .Select(e => ToServiceProviderExpenseIssue(e, "Service provider expense has no ledger entry."))
            .ToList();

        var serviceProviderPaymentsWithoutLedger = serviceProviderPaymentRows
            .Where(p => !p.LedgerEntryId.HasValue || p.LedgerEntry is null)
            .Select(p => ToServiceProviderPaymentIssue(p, "Service provider payment has no ledger entry."))
            .ToList();

        var serviceProviderLedgerMissingSourceRows = await _db.LedgerEntries
            .AsNoTracking()
            .Include(l => l.ServiceProvider)
            .Where(l => l.ServiceProviderId.HasValue
                && (l.SourceId <= 0 || string.IsNullOrWhiteSpace(l.SourceType) || string.IsNullOrWhiteSpace(l.Reference)))
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync();
        var serviceProviderLedgerMissingSource = serviceProviderLedgerMissingSourceRows
            .Select(l => ToServiceProviderLedgerIssue(l, "Service provider ledger entry has incomplete source/reference trace."))
            .ToList();

        var serviceProviderPaymentLedgerMismatches = serviceProviderPaymentRows
            .Where(p => p.LedgerEntry is not null
                && (p.LedgerEntry.ServiceProviderId != p.ServiceProviderId
                    || p.LedgerEntry.SourceId != p.Id
                    || p.LedgerEntry.SourceType != p.PaymentKind.ToString()
                    || decimal.Round(p.LedgerEntry.AmountUsd, 4, MidpointRounding.AwayFromZero)
                        != decimal.Round(p.AmountUsd, 4, MidpointRounding.AwayFromZero)))
            .Select(p => ToServiceProviderPaymentIssue(p, "Service provider payment amount/source does not match linked ledger."))
            .ToList();

        var serviceProviderExpenseLedgerMismatches = serviceProviderExpenseRows
            .Where(e => !e.IsCancelled)
            .Select(e => new
            {
                Expense = e,
                Ledgers = serviceProviderExpenseLedgerBySource.GetValueOrDefault(e.Id) ?? []
            })
            .Where(x => x.Ledgers.Count > 0
                && (x.Ledgers.Any(l => l.ServiceProviderId != x.Expense.ServiceProviderId)
                    || decimal.Round(x.Ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd), 4, MidpointRounding.AwayFromZero)
                        != decimal.Round(x.Expense.AmountUsd, 4, MidpointRounding.AwayFromZero)))
            .Select(x => ToServiceProviderExpenseIssue(
                x.Expense,
                "Service provider expense amount/source does not match ledger.",
                x.Ledgers.FirstOrDefault(),
                x.Ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd)))
            .ToList();

        var cancelledServiceProviderExpensesWithBalanceImpact = serviceProviderExpenseRows
            .Where(e => e.IsCancelled)
            .Select(e => new
            {
                Expense = e,
                Ledgers = serviceProviderExpenseLedgerBySource.GetValueOrDefault(e.Id) ?? []
            })
            .Where(x => x.Ledgers.Count > 0
                && decimal.Round(x.Ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd), 4, MidpointRounding.AwayFromZero) != 0m)
            .Select(x => ToServiceProviderExpenseIssue(
                x.Expense,
                "Cancelled service provider expense still has non-zero ledger balance impact.",
                x.Ledgers.FirstOrDefault(),
                x.Ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd),
                "Warning"))
            .ToList();

        var sarrafSettlementRows = await _db.SarrafSettlements
            .AsNoTracking()
            .Include(s => s.Sarraf)
            .Include(s => s.Supplier)
            .Include(s => s.Contract)
            .Include(s => s.LedgerEntry)
            .Include(s => s.ExchangeDifferenceLedgerEntry)
            .Where(s => s.Status == SarrafSettlementStatus.Posted)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        SarrafSettlementReconciliationItemViewModel ToSarrafSettlementIssue(
            SarrafSettlement settlement,
            string issue,
            string status = "Needs Review")
            => new()
            {
                SettlementId = settlement.Id,
                SettlementDate = settlement.SettlementDate,
                SarrafName = settlement.Sarraf?.Name ?? "-",
                SupplierName = settlement.Supplier?.Name ?? settlement.Contract?.Supplier?.Name ?? "-",
                ContractNumber = settlement.Contract?.ContractNumber ?? "-",
                Reference = settlement.ReferenceNumber ?? ("Sarraf settlement #" + settlement.Id),
                RequestedAmountUsd = settlement.RequestedAmountUsd,
                SupplierLedgerAmountUsd = ExpectedSarrafSupplierLedgerAmountUsd(settlement),
                LedgerAmountUsd = settlement.LedgerEntry?.AmountUsd,
                LedgerEntryId = settlement.LedgerEntryId,
                DifferenceAmountUsd = settlement.DifferenceAmountUsd,
                DifferenceLedgerAmountUsd = settlement.ExchangeDifferenceLedgerEntry?.AmountUsd,
                ExchangeDifferenceLedgerEntryId = settlement.ExchangeDifferenceLedgerEntryId,
                Issue = issue,
                Status = status
            };

        var sarrafSettlementsWithoutSupplierLedger = sarrafSettlementRows
            .Where(s => !s.LedgerEntryId.HasValue || s.LedgerEntry is null)
            .Select(s => ToSarrafSettlementIssue(s, "Sarraf settlement has no supplier ledger entry."))
            .ToList();

        var sarrafSettlementSupplierLedgerMismatches = sarrafSettlementRows
            .Where(s => s.LedgerEntry is not null
                && (s.LedgerEntry.SourceType != SarrafSettlementService.SupplierLedgerSourceType
                    || s.LedgerEntry.SourceId != s.Id
                    || s.LedgerEntry.Side != LedgerSide.Debit
                    || s.LedgerEntry.SupplierId != s.SupplierId
                    || s.LedgerEntry.ContractId != s.ContractId
                    || decimal.Round(s.LedgerEntry.AmountUsd, 4, MidpointRounding.AwayFromZero)
                        != decimal.Round(ExpectedSarrafSupplierLedgerAmountUsd(s), 4, MidpointRounding.AwayFromZero)))
            .Select(s => ToSarrafSettlementIssue(s, "Sarraf settlement supplier ledger amount/source does not match settlement."))
            .ToList();

        var sarrafSettlementsRequiringDifferenceLedger = sarrafSettlementRows
            .Where(RequiresSarrafDifferenceLedger)
            .ToList();

        var sarrafSettlementsWithoutDifferenceLedger = sarrafSettlementsRequiringDifferenceLedger
            .Where(s => !s.ExchangeDifferenceLedgerEntryId.HasValue || s.ExchangeDifferenceLedgerEntry is null)
            .Select(s => ToSarrafSettlementIssue(s, "Recognized sarraf exchange difference has no ledger entry."))
            .ToList();

        var sarrafSettlementDifferenceLedgerMismatches = sarrafSettlementsRequiringDifferenceLedger
            .Where(s => s.ExchangeDifferenceLedgerEntry is not null
                && (s.ExchangeDifferenceLedgerEntry.SourceType != SarrafSettlementService.ExchangeDifferenceSourceType
                    || s.ExchangeDifferenceLedgerEntry.SourceId != s.Id
                    || s.ExchangeDifferenceLedgerEntry.ContractId != s.ContractId
                    || s.ExchangeDifferenceLedgerEntry.Side != ExpectedSarrafDifferenceLedgerSide(s)
                    || decimal.Round(s.ExchangeDifferenceLedgerEntry.AmountUsd, 4, MidpointRounding.AwayFromZero)
                        != decimal.Round(Math.Abs(s.DifferenceAmountUsd), 4, MidpointRounding.AwayFromZero)))
            .Select(s => ToSarrafSettlementIssue(s, "Sarraf exchange difference ledger amount/source does not match settlement."))
            .ToList();

        var assetRentRows = await _db.AssetRentTransactions
            .AsNoTracking()
            .Include(r => r.OperationalAsset)
            .Include(r => r.RentShares)
            .Include(r => r.LedgerEntry)
            .Where(r => !r.IsCancelled)
            .OrderByDescending(r => r.RentDate)
            .ThenByDescending(r => r.Id)
            .ToListAsync();

        OperationalAssetReconciliationItemViewModel ToAssetRentIssue(
            AssetRentTransaction rent,
            string issue,
            decimal? relatedAmountUsd = null,
            string status = "Needs Review")
            => new()
            {
                SourceId = rent.Id,
                SourceType = "AssetRentTransaction",
                Date = rent.RentDate,
                OperationalAssetId = rent.OperationalAssetId,
                OperationalAssetName = rent.OperationalAsset is null
                    ? "Asset #" + rent.OperationalAssetId
                    : rent.OperationalAsset.AssetCode + " - " + rent.OperationalAsset.Name,
                Reference = rent.ReferenceDocument ?? ("Asset rent #" + rent.Id),
                AmountUsd = rent.AmountUsd,
                RelatedAmountUsd = relatedAmountUsd,
                Issue = issue,
                Status = status
            };

        OperationalAssetReconciliationItemViewModel ToAssetIssue(
            OperationalAsset asset,
            string issue,
            string status = "Needs Review")
            => new()
            {
                SourceId = asset.Id,
                SourceType = "OperationalAsset",
                Date = asset.CreatedAtUtc.Date,
                OperationalAssetId = asset.Id,
                OperationalAssetName = asset.AssetCode + " - " + asset.Name,
                Reference = asset.AssetCode,
                AmountUsd = 0m,
                Issue = issue,
                Status = status
            };

        OperationalAssetReconciliationItemViewModel ToAssetExpenseIssue(
            ExpenseTransaction expense,
            string issue,
            string status = "Needs Review")
            => new()
            {
                SourceId = expense.Id,
                SourceType = "Expense",
                Date = expense.ExpenseDate,
                OperationalAssetId = expense.OperationalAssetId,
                OperationalAssetName = expense.OperationalAsset is null
                    ? (expense.OperationalAssetId.HasValue ? "Asset #" + expense.OperationalAssetId : "—")
                    : expense.OperationalAsset.AssetCode + " - " + expense.OperationalAsset.Name,
                Reference = expense.Description ?? ("Expense #" + expense.Id),
                AmountUsd = expense.AmountUsd,
                Issue = issue,
                Status = status
            };

        var assetRentTransactionsWithoutShares = assetRentRows
            .Where(r => r.RentShares.Count == 0)
            .Select(r => ToAssetRentIssue(r, "Asset rent transaction has no owner share snapshots."))
            .ToList();

        var assetRentShareSumMismatches = assetRentRows
            .Where(r => r.RentShares.Count > 0
                && decimal.Round(r.RentShares.Sum(s => s.ShareAmountUsd), 4, MidpointRounding.AwayFromZero)
                    != decimal.Round(r.AmountUsd, 4, MidpointRounding.AwayFromZero))
            .Select(r => ToAssetRentIssue(
                r,
                "Asset rent share total does not match rent amount.",
                r.RentShares.Sum(s => s.ShareAmountUsd)))
            .ToList();

        var assetIdsWithRent = assetRentRows.Select(r => r.OperationalAssetId).Distinct().ToArray();
        var ownershipRowsForRentAssets = assetIdsWithRent.Length == 0
            ? new List<AssetOwnershipShare>()
            : await _db.AssetOwnershipShares
                .AsNoTracking()
                .Where(s => assetIdsWithRent.Contains(s.OperationalAssetId))
                .ToListAsync();

        var assetRentOwnershipCoverageIssues = assetRentRows
            .Select(r => new
            {
                Rent = r,
                ActiveSharePercent = ownershipRowsForRentAssets
                    .Where(s => s.OperationalAssetId == r.OperationalAssetId
                        && s.EffectiveFrom <= r.RentDate
                        && (!s.EffectiveTo.HasValue || s.EffectiveTo.Value >= r.RentDate))
                    .Sum(s => s.SharePercent)
            })
            .Where(x => decimal.Round(x.ActiveSharePercent, 4, MidpointRounding.AwayFromZero) != 100m)
            .Select(x => ToAssetRentIssue(
                x.Rent,
                "Active ownership shares do not total 100% on rent date.",
                x.ActiveSharePercent,
                "Warning"))
            .ToList();

        var operationalAssetRows = await _db.OperationalAssets
            .AsNoTracking()
            .Include(a => a.LinkedTruck)
            .Include(a => a.LinkedStorageTank)
            .Where(a => a.LinkedTruckId.HasValue || a.LinkedStorageTankId.HasValue)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
        var operationalAssetLinkIssues = operationalAssetRows
            .SelectMany(asset =>
            {
                var issues = new List<OperationalAssetReconciliationItemViewModel>();
                if (asset.LinkedTruckId.HasValue && asset.LinkedTruck is null)
                {
                    issues.Add(ToAssetIssue(asset, "Operational asset is linked to a missing truck."));
                }
                else if (asset.LinkedTruck is not null && !asset.LinkedTruck.IsActive)
                {
                    issues.Add(ToAssetIssue(asset, "Operational asset is linked to an inactive truck.", "Warning"));
                }

                if (asset.LinkedStorageTankId.HasValue && asset.LinkedStorageTank is null)
                {
                    issues.Add(ToAssetIssue(asset, "Operational asset is linked to a missing storage tank."));
                }
                else if (asset.LinkedStorageTank is not null && !asset.LinkedStorageTank.IsActive)
                {
                    issues.Add(ToAssetIssue(asset, "Operational asset is linked to an inactive storage tank.", "Warning"));
                }

                return issues;
            })
            .ToList();

        var assetExpenseInactiveAssetRows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.OperationalAsset)
            .Where(e => e.OperationalAssetId.HasValue
                && !e.IsCancelled
                && (e.OperationalAsset == null || !e.OperationalAsset.IsActive))
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();
        var assetExpenseInactiveAssetIssues = assetExpenseInactiveAssetRows
            .Select(e => ToAssetExpenseIssue(
                e,
                e.OperationalAsset == null
                    ? "Expense is linked to a missing operational asset."
                    : "Expense is linked to an inactive operational asset.",
                e.OperationalAsset == null ? "Needs Review" : "Warning"))
            .ToList();

        var assetRentPostedWithoutLedger = assetRentRows
            .Where(r => r.IsPostedToLedger && (!r.LedgerEntryId.HasValue || r.LedgerEntry is null))
            .Select(r => ToAssetRentIssue(r, "Asset rent is marked posted but has no ledger entry."))
            .ToList();

        var assetRentContractRequirementIssues = assetRentRows
            .Where(r => (r.ChargedToType == AssetRentChargedToType.PurchaseContract
                    || r.ChargedToType == AssetRentChargedToType.SalesContract)
                && !r.ChargedToContractId.HasValue)
            .Select(r => ToAssetRentIssue(r, "Asset rent charged-to type requires a contract."))
            .ToList();

        var assetRentDuplicateCandidates = assetRentRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ReferenceDocument))
            .GroupBy(r => new { r.OperationalAssetId, RentDate = r.RentDate.Date, Reference = r.ReferenceDocument!.Trim() })
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => ToAssetRentIssue(r, "Suspicious duplicate asset rent transaction by same asset/date/reference.", status: "Warning")))
            .ToList();

        var directSaleAllocationsWithoutSale = directSaleAllocations
            .Where(a => !a.SalesTransactionId.HasValue)
            .Select(a => ToDirectSaleReconciliationItem(a, "Missing SalesTransaction"))
            .ToList();

        var directSaleSalesWithoutLedger = directSaleAllocations
            .Where(a => a.SalesTransaction is not null
                && !a.SalesTransaction.IsCancelled
                && !directSaleLedgerAmountBySaleId.ContainsKey(a.SalesTransaction.Id))
            .Select(a => ToDirectSaleReconciliationItem(a, "Missing Sale Ledger"))
            .ToList();

        var directSaleQuantityMismatches = directSaleAllocations
            .Where(a => a.SalesTransaction is not null
                && !a.SalesTransaction.IsCancelled
                && a.QuantityMt != a.SalesTransaction.QuantityMt)
            .Select(a =>
            {
                directSaleLedgerAmountBySaleId.TryGetValue(a.SalesTransaction!.Id, out var ledgerAmountUsd);
                return ToDirectSaleReconciliationItem(a, "Quantity mismatch", ledgerAmountUsd);
            })
            .ToList();

        var directSaleLedgerAmountMismatches = directSaleAllocations
            .Where(a => a.SalesTransaction is not null
                && !a.SalesTransaction.IsCancelled
                && directSaleLedgerAmountBySaleId.TryGetValue(a.SalesTransaction.Id, out var ledgerAmountUsd)
                && decimal.Round(ledgerAmountUsd, 4, MidpointRounding.AwayFromZero)
                    != decimal.Round(a.SalesTransaction.TotalUsd, 4, MidpointRounding.AwayFromZero))
            .Select(a => ToDirectSaleReconciliationItem(
                a,
                "Ledger amount mismatch",
                directSaleLedgerAmountBySaleId[a.SalesTransaction!.Id]))
            .ToList();

        var directDispatchAllocationsWithoutDispatch = directDispatchAllocations
            .Where(a => !GetActiveDirectFromReceiptDispatches(a).Any())
            .Select(a => ToDirectDispatchReconciliationItem(a, "Missing DirectFromReceipt TruckDispatch"))
            .ToList();

        var directDispatchQuantityMismatches = directDispatchAllocations
            .Where(a =>
            {
                var activeDispatches = GetActiveDirectFromReceiptDispatches(a);
                return activeDispatches.Any()
                    && !QuantitiesMatch(a.QuantityMt, activeDispatches.Sum(d => d.LoadedQuantityMt));
            })
            .Select(a => ToDirectDispatchReconciliationItem(a, "Quantity mismatch"))
            .ToList();

        var directDispatchesWithoutAllocation = directFromReceiptDispatches
            .Where(d => !d.LoadingReceiptAllocationId.HasValue)
            .Select(d => ToDirectDispatchReconciliationItem(d, "Missing LoadingReceiptAllocation link"))
            .ToList();

        var directDispatchesWithInventoryMovement = directFromReceiptDispatches
            .Where(d => directDispatchIdsWithInventoryMovement.Contains(d.Id))
            .Select(d => ToDirectDispatchReconciliationItem(
                d,
                "Unexpected InventoryMovement",
                d.LoadingReceiptAllocationId.HasValue
                    && directDispatchAllocationById.TryGetValue(d.LoadingReceiptAllocationId.Value, out var allocation)
                        ? allocation
                        : null))
            .ToList();

        var directDispatchStatusMismatches = directDispatchAllocations
            .Where(HasDirectDispatchStatusMismatch)
            .Select(a => ToDirectDispatchReconciliationItem(a, "Status mismatch"))
            .ToList();

        var directDispatchSalesWithoutLedger = directFromReceiptDispatches
            .Where(d => d.SalesTransaction is not null
                && !d.SalesTransaction.IsCancelled
                && !directDispatchLedgerAmountBySaleId.ContainsKey(d.SalesTransaction.Id))
            .Select(d => ToDirectDispatchReconciliationItem(d, "Missing Sale Ledger"))
            .ToList();

        var directDispatchSaleQuantityMismatches = directFromReceiptDispatches
            .Where(d => d.SalesTransaction is not null
                && !d.SalesTransaction.IsCancelled
                && !QuantitiesMatch(d.LoadedQuantityMt, d.SalesTransaction.QuantityMt))
            .Select(d =>
            {
                directDispatchLedgerAmountBySaleId.TryGetValue(d.SalesTransaction!.Id, out var ledgerAmountUsd);
                return ToDirectDispatchReconciliationItem(d, "Dispatch/Sale quantity mismatch", ledgerAmountUsd: ledgerAmountUsd);
            })
            .ToList();

        // ── Inventory integrity (read-only) ───────────────────────────────
        // 1) Inventory movements that reference a non-Purchase contract.
        //    Stock-out movements must always trace to the originating Purchase
        //    contract; a Sales-contract reference here corrupts per-contract
        //    stock balances and ContractPnl revenue allocation.
        var inventoryMovementsWithNonPurchaseContract = await _db.InventoryMovements
            .AsNoTracking()
            .Include(m => m.Contract)
            .Include(m => m.Product)
            .Include(m => m.Terminal)
            .Where(m => m.ContractId.HasValue
                && m.Contract != null
                && m.Contract.ContractType != ContractType.Purchase)
            .OrderByDescending(m => m.MovementDate)
            .ThenByDescending(m => m.Id)
            .Select(m => new InventoryMovementContractIssueViewModel
            {
                MovementId = m.Id,
                MovementDate = m.MovementDate,
                ContractId = m.ContractId!.Value,
                ContractNumber = m.Contract!.ContractNumber,
                ContractType = m.Contract.ContractType.ToString(),
                ProductName = m.Product != null ? m.Product.Name : "",
                TerminalName = m.Terminal != null ? m.Terminal.Name : "",
                DirectionText = m.Direction.ToString(),
                QuantityMt = m.QuantityMt,
                ReferenceDocument = m.ReferenceDocument,
                Issue = "Inventory movement uses non-purchase contract"
            })
            .ToListAsync();

        // 2) ToInventory allocations that have no InventoryMovement linked, or
        //    whose linked InventoryMovementId no longer points at a real row.
        var existingMovementIds = await _db.InventoryMovements
            .AsNoTracking()
            .Select(m => m.Id)
            .ToListAsync();
        var existingMovementIdSet = existingMovementIds.ToHashSet();

        var toInventoryAllocationsWithoutMovement = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Include(a => a.LoadingReceipt)
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.Terminal)
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.ToInventory)
            .OrderBy(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.LoadingReceiptId,
                ReceiptDate = a.LoadingReceipt != null ? a.LoadingReceipt.ReceiptDate : (DateTime?)null,
                ContractNumber = a.SourcePurchaseContract != null
                    ? a.SourcePurchaseContract.ContractNumber
                    : (a.SourcePurchaseContractId.HasValue ? "Contract #" + a.SourcePurchaseContractId : ""),
                TerminalName = a.Terminal != null ? a.Terminal.Name : "",
                a.QuantityMt,
                a.InventoryMovementId
            })
            .ToListAsync();

        var toInventoryAllocationsWithoutMovementRows = toInventoryAllocationsWithoutMovement
            .Where(a => !a.InventoryMovementId.HasValue
                || !existingMovementIdSet.Contains(a.InventoryMovementId.Value))
            .Select(a => new ToInventoryAllocationIssueViewModel
            {
                AllocationId = a.Id,
                LoadingReceiptId = a.LoadingReceiptId,
                ReceiptDate = a.ReceiptDate ?? default,
                ContractNumber = a.ContractNumber,
                TerminalName = a.TerminalName,
                AllocationQuantityMt = a.QuantityMt,
                InventoryMovementId = a.InventoryMovementId,
                Issue = a.InventoryMovementId.HasValue
                    ? "ToInventory allocation references missing InventoryMovement"
                    : "ToInventory allocation has no inventory movement"
            })
            .ToList();

        // 3) ToInventory receipts that have no LoadingReceiptAllocation rows.
        //    Without an allocation, downstream traces (stock, PnL) cannot be
        //    reconstructed even if a stand-alone movement was created.
        var toInventoryReceiptsWithoutAllocationRaw = await _db.LoadingReceipts
            .AsNoTracking()
            .Include(r => r.LoadingRegister)
                .ThenInclude(lr => lr!.Contract)
            .Include(r => r.Terminal)
            .Where(r => r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                && !r.Allocations.Any())
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new ToInventoryReceiptIssueViewModel
            {
                LoadingReceiptId = r.Id,
                ReceiptDate = r.ReceiptDate,
                ContractNumber = r.LoadingRegister != null && r.LoadingRegister.Contract != null
                    ? r.LoadingRegister.Contract.ContractNumber
                    : "",
                TerminalName = r.Terminal != null ? r.Terminal.Name : "",
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                Issue = "ToInventory receipt has no allocation"
            })
            .ToListAsync();

        // 4) Duplicate customs candidates: contracts that have BOTH a CustomsDeclaration
        //    total > 0 and an ExpenseTransaction with a customs-flavoured ExpenseType
        //    (Category/Code/Name contains "customs" case-insensitive, or NamePersian
        //    contains "گمرک"). The two paths feed ContractPnl independently
        //    (CustomsCostUsd vs GeneralExpenseCostUsd) so a row recorded on both can
        //    silently inflate cost. We only WARN — no data is changed.
        // ExpenseType lookup table is small; fetch and filter in memory so the
        // case-insensitive match works on both PostgreSQL and the InMemory test provider.
        var allExpenseTypes = await _db.ExpenseTypes
            .AsNoTracking()
            .Select(et => new { et.Id, et.Category, et.Code, et.Name, et.NamePersian })
            .ToListAsync();
        var customsExpenseTypeIds = allExpenseTypes
            .Where(et =>
                (et.Category?.Contains("customs", StringComparison.OrdinalIgnoreCase) ?? false)
                || (et.Code?.Contains("customs", StringComparison.OrdinalIgnoreCase) ?? false)
                || (et.Name?.Contains("customs", StringComparison.OrdinalIgnoreCase) ?? false)
                || (et.NamePersian?.Contains("گمرک") ?? false))
            .Select(et => et.Id)
            .ToList();

        var customsSourceIssues = await _db.CustomsDeclarations
            .AsNoTracking()
            .Where(cd => (cd.LoadingRegisterId.HasValue && cd.TransportLegId.HasValue)
                || (!cd.LoadingRegisterId.HasValue && !cd.TransportLegId.HasValue)
                || (cd.TransportLegId.HasValue && cd.TransportLeg == null))
            .OrderBy(cd => cd.Id)
            .Select(cd => new CustomsSourceIssueViewModel
            {
                CustomsDeclarationId = cd.Id,
                LoadingRegisterId = cd.LoadingRegisterId,
                TransportLegId = cd.TransportLegId,
                DeclarationDate = cd.DeclarationDate,
                WagonOrTruckNumber = cd.WagonOrTruckNumber,
                Issue = cd.TransportLegId.HasValue && cd.TransportLeg == null
                    ? "Customs declaration references missing transport leg."
                    : "Customs declaration must reference exactly one source."
            })
            .ToListAsync();

        var loadingCustomsDeclTotalsByContract = await _db.CustomsDeclarations
            .AsNoTracking()
            .Include(cd => cd.LoadingRegister)
            .Where(cd => cd.LoadingRegisterId.HasValue
                && !cd.TransportLegId.HasValue
                && cd.LoadingRegister != null
                && cd.TotalUsd > 0m)
            .GroupBy(cd => cd.LoadingRegister!.ContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                Total = g.Sum(cd => cd.TotalUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var transportLegCustomsDeclTotalsByContract = await _db.CustomsDeclarations
            .AsNoTracking()
            .Include(cd => cd.TransportLeg)
            .Where(cd => cd.TransportLegId.HasValue
                && !cd.LoadingRegisterId.HasValue
                && cd.TransportLeg != null
                && cd.TotalUsd > 0m)
            .GroupBy(cd => cd.TransportLeg!.SourcePurchaseContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                Total = g.Sum(cd => cd.TotalUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var customsDeclTotalsByContract = loadingCustomsDeclTotalsByContract
            .Concat(transportLegCustomsDeclTotalsByContract)
            .GroupBy(x => x.ContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                Total = g.Sum(x => x.Total),
                Count = g.Sum(x => x.Count)
            })
            .ToList();

        var customsExpenseTotalsByContract = customsExpenseTypeIds.Count == 0
            ? new List<(int ContractId, decimal Total, int Count)>()
            : (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => !e.IsCancelled
                    && e.ContractId.HasValue
                    && e.AmountUsd > 0m
                    && customsExpenseTypeIds.Contains(e.ExpenseTypeId))
                .GroupBy(e => e.ContractId!.Value)
                .Select(g => new
                {
                    ContractId = g.Key,
                    Total = g.Sum(e => e.AmountUsd),
                    Count = g.Count()
                })
                .ToListAsync())
                .Select(x => (x.ContractId, x.Total, x.Count))
                .ToList();

        var declMap = customsDeclTotalsByContract.ToDictionary(x => x.ContractId, x => (x.Total, x.Count));
        var expenseMap = customsExpenseTotalsByContract.ToDictionary(x => x.ContractId, x => (x.Total, x.Count));
        var overlappingContractIds = declMap.Keys.Intersect(expenseMap.Keys).ToList();

        List<DuplicateCustomsIssueViewModel> duplicateCustomsCandidates;
        if (overlappingContractIds.Count == 0)
        {
            duplicateCustomsCandidates = [];
        }
        else
        {
            var contractRows = await _db.Contracts.AsNoTracking()
                .Where(c => overlappingContractIds.Contains(c.Id))
                .OrderBy(c => c.ContractNumber)
                .Select(c => new { c.Id, c.ContractNumber, c.ContractType })
                .ToListAsync();
            duplicateCustomsCandidates = contractRows
                .Select(c => new DuplicateCustomsIssueViewModel
                {
                    ContractId = c.Id,
                    ContractNumber = c.ContractNumber,
                    ContractType = c.ContractType.ToString(),
                    CustomsDeclarationTotalUsd = declMap[c.Id].Total,
                    CustomsDeclarationCount = declMap[c.Id].Count,
                    CustomsExpenseTotalUsd = expenseMap[c.Id].Total,
                    CustomsExpenseCount = expenseMap[c.Id].Count,
                    Issue = "این قرارداد هم CustomsDeclaration دارد و هم ExpenseTransaction گمرکی؛ احتمال دوباره‌شماری وجود دارد."
                })
                .ToList();
        }

        var inventoryTransportLegIssues = await BuildInventoryTransportLegIssuesAsync();

        return new MissingLedgerViewModel
        {
            SalesWithoutLedger = sales,
            ExpensesWithoutLedger = expenses,
            PaymentsWithoutLedger = payments,
            SupplierPaymentsWithoutSupplier = supplierPaymentsWithoutSupplier,
            SupplierPaymentContractSupplierMismatches = supplierPaymentContractSupplierMismatches,
            SupplierPaymentLedgerMissingSupplierOrContract = supplierPaymentLedgerMissingSupplierOrContract,
            SupplierPaymentsWithoutLedger = supplierPaymentsWithoutLedger,
            SupplierLedgerFxIssues = supplierLedgerFxIssues,
            ServiceProviderExpensesWithoutLedger = serviceProviderExpensesWithoutLedger,
            ServiceProviderPaymentsWithoutLedger = serviceProviderPaymentsWithoutLedger,
            ServiceProviderLedgerMissingSource = serviceProviderLedgerMissingSource,
            ServiceProviderPaymentLedgerMismatches = serviceProviderPaymentLedgerMismatches,
            ServiceProviderExpenseLedgerMismatches = serviceProviderExpenseLedgerMismatches,
            CancelledServiceProviderExpensesWithBalanceImpact = cancelledServiceProviderExpensesWithBalanceImpact,
            SarrafSettlementsWithoutSupplierLedger = sarrafSettlementsWithoutSupplierLedger,
            SarrafSettlementSupplierLedgerMismatches = sarrafSettlementSupplierLedgerMismatches,
            SarrafSettlementsWithoutDifferenceLedger = sarrafSettlementsWithoutDifferenceLedger,
            SarrafSettlementDifferenceLedgerMismatches = sarrafSettlementDifferenceLedgerMismatches,
            AssetRentTransactionsWithoutShares = assetRentTransactionsWithoutShares,
            AssetRentShareSumMismatches = assetRentShareSumMismatches,
            AssetRentOwnershipCoverageIssues = assetRentOwnershipCoverageIssues,
            OperationalAssetLinkIssues = operationalAssetLinkIssues,
            AssetExpenseInactiveAssetIssues = assetExpenseInactiveAssetIssues,
            AssetRentPostedWithoutLedger = assetRentPostedWithoutLedger,
            AssetRentContractRequirementIssues = assetRentContractRequirementIssues,
            AssetRentDuplicateCandidates = assetRentDuplicateCandidates,
            DirectSaleAllocationsWithoutSale = directSaleAllocationsWithoutSale,
            DirectSaleSalesWithoutLedger = directSaleSalesWithoutLedger,
            DirectSaleQuantityMismatches = directSaleQuantityMismatches,
            DirectSaleLedgerAmountMismatches = directSaleLedgerAmountMismatches,
            DirectDispatchAllocationsWithoutDispatch = directDispatchAllocationsWithoutDispatch,
            DirectDispatchQuantityMismatches = directDispatchQuantityMismatches,
            DirectDispatchesWithoutAllocation = directDispatchesWithoutAllocation,
            DirectDispatchesWithInventoryMovement = directDispatchesWithInventoryMovement,
            DirectDispatchStatusMismatches = directDispatchStatusMismatches,
            DirectDispatchSalesWithoutLedger = directDispatchSalesWithoutLedger,
            DirectDispatchSaleQuantityMismatches = directDispatchSaleQuantityMismatches,
            InventoryMovementsWithNonPurchaseContract = inventoryMovementsWithNonPurchaseContract,
            ToInventoryAllocationsWithoutMovement = toInventoryAllocationsWithoutMovementRows,
            ToInventoryReceiptsWithoutAllocation = toInventoryReceiptsWithoutAllocationRaw,
            DuplicateCustomsCandidates = duplicateCustomsCandidates,
            CustomsSourceIssues = customsSourceIssues,
            InventoryTransportLegIssues = inventoryTransportLegIssues
        };
    }

    private static decimal ExpectedSarrafSupplierLedgerAmountUsd(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            ? settlement.RequestedAmountUsd
            : settlement.SupplierAcceptedAmountUsd;

    private static bool RequiresSarrafDifferenceLedger(SarrafSettlement settlement)
        => settlement.DifferenceTreatment == SarrafSettlementDifferenceTreatment.RecognizeExchangeGainLoss
            && settlement.DifferenceType != SarrafSettlementDifferenceType.None
            && settlement.DifferenceAmountUsd != 0m;

    private static LedgerSide ExpectedSarrafDifferenceLedgerSide(SarrafSettlement settlement)
        => settlement.DifferenceType == SarrafSettlementDifferenceType.Gain
            ? LedgerSide.Credit
            : LedgerSide.Debit;

    private async Task<IReadOnlyList<InventoryTransportLegIssueViewModel>> BuildInventoryTransportLegIssuesAsync()
    {
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.OutboundInventoryMovement)
            .Where(l => l.Status == InventoryTransportLegStatus.Loaded
                || l.Status == InventoryTransportLegStatus.InTransit
                || l.Status == InventoryTransportLegStatus.Received
                || l.Status == InventoryTransportLegStatus.Cancelled
                || l.OutboundInventoryMovementId.HasValue)
            .OrderBy(l => l.Id)
            .ToListAsync();

        var legIds = legs.Select(l => l.Id).ToList();
        var contractIds = legs.Select(l => l.SourcePurchaseContractId).Distinct().ToList();
        var contractFinalPriceById = contractIds.Count == 0
            ? new Dictionary<int, decimal?>()
            : await _db.Contracts
                .AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .Select(c => new
                {
                    c.Id,
                    FinalPriceUsd = c.ManualFinalPriceUsd.HasValue && c.ManualFinalPriceUsd.Value > 0m
                        ? c.ManualFinalPriceUsd
                        : c.UnitPriceUsd.HasValue && c.UnitPriceUsd.Value > 0m
                            ? c.UnitPriceUsd
                            : null
                })
                .ToDictionaryAsync(x => x.Id, x => x.FinalPriceUsd);
        var purchaseAggByContract = await _purchaseAggregation.AggregateForContractsAsync(contractIds, contractFinalPriceById);

        var activeReceipts = legIds.Count == 0
            ? new List<InventoryTransportReceipt>()
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
                .ToListAsync();
        var receiptsByLegId = activeReceipts
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var activeExpenseLegIds = legIds.Count == 0
            ? new HashSet<int>()
            : (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.TransportLegId.HasValue
                    && legIds.Contains(e.TransportLegId.Value)
                    && !e.IsCancelled)
                .Select(e => e.TransportLegId!.Value)
                .Distinct()
                .ToListAsync())
                .ToHashSet();
        var expensesMissingContractLegIds = legIds.Count == 0
            ? new HashSet<int>()
            : (await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.TransportLegId.HasValue
                    && legIds.Contains(e.TransportLegId.Value)
                    && !e.IsCancelled
                    && !e.ContractId.HasValue)
                .Select(e => e.TransportLegId!.Value)
                .Distinct()
                .ToListAsync())
                .ToHashSet();

        var activeCustomsLegIds = legIds.Count == 0
            ? new HashSet<int>()
            : (await _db.CustomsDeclarations
                .AsNoTracking()
                .Where(cd => cd.TransportLegId.HasValue && legIds.Contains(cd.TransportLegId.Value))
                .Select(cd => cd.TransportLegId!.Value)
                .Distinct()
                .ToListAsync())
                .ToHashSet();

        var activeLosses = legIds.Count == 0
            ? new List<LossEvent>()
            : await _db.LossEvents
                .AsNoTracking()
                .Where(le => le.TransportLegId.HasValue
                    && legIds.Contains(le.TransportLegId.Value)
                    && !le.IsCancelled)
                .ToListAsync();
        var activeLossLegIds = activeLosses
            .Select(le => le.TransportLegId!.Value)
            .Distinct()
            .ToHashSet();
        var unvaluedLossLegIds = activeLosses
            .Where(le => le.ChargeableLossMt > 0m)
            .Where(le =>
            {
                var leg = legs.FirstOrDefault(l => l.Id == le.TransportLegId);
                if (leg is null)
                {
                    return true;
                }

                return !purchaseAggByContract.TryGetValue(leg.SourcePurchaseContractId, out var agg)
                    || !IPurchaseAggregationService.HasValidLoadingPrice(agg.WeightedAveragePurchasePriceUsd);
            })
            .Select(le => le.TransportLegId!.Value)
            .Distinct()
            .ToHashSet();

        var issues = new List<InventoryTransportLegIssueViewModel>();

        foreach (var leg in legs)
        {
            var issue = ResolveInventoryTransportLegIssue(leg);
            if (issue is not null)
            {
                issues.Add(ToInventoryTransportLegIssue(leg, issue.Value.Issue, issue.Value.Status));
            }

            receiptsByLegId.TryGetValue(leg.Id, out var legReceipts);
            if (leg.Status == InventoryTransportLegStatus.Received
                && (legReceipts is null || legReceipts.Count == 0))
            {
                issues.Add(ToInventoryTransportLegIssue(
                    leg,
                    "Transport leg is received but has no destination receipt.",
                    "Needs Review"));
            }

            if (legReceipts is not null
                && legReceipts.Any(r => !QuantitiesMatch(r.ReceivedQuantityMt + r.ShortageQuantityMt, leg.QuantityMt)))
            {
                issues.Add(ToInventoryTransportLegIssue(
                    leg,
                    "Destination receipt quantity does not match transport leg quantity.",
                    "Needs Review"));
            }

            if (expensesMissingContractLegIds.Contains(leg.Id))
            {
                issues.Add(ToInventoryTransportLegIssue(
                    leg,
                    "Transport leg expense has no purchase contract for P&L.",
                    "Needs Review"));
            }

            if (unvaluedLossLegIds.Contains(leg.Id))
            {
                issues.Add(ToInventoryTransportLegIssue(
                    leg,
                    "Transport leg loss cannot be valued from source purchase price.",
                    "Needs Review"));
            }

            if (leg.Status == InventoryTransportLegStatus.Cancelled
                && ((legReceipts?.Count ?? 0) > 0
                    || activeCustomsLegIds.Contains(leg.Id)
                    || activeExpenseLegIds.Contains(leg.Id)
                    || activeLossLegIds.Contains(leg.Id)))
            {
                issues.Add(ToInventoryTransportLegIssue(
                    leg,
                    "Cancelled transport leg has active linked records.",
                    "Warning"));
            }
        }

        return issues;
    }

    private static InventoryTransportLegIssueViewModel ToInventoryTransportLegIssue(
        InventoryTransportLeg leg,
        string issue,
        string status)
        => new()
        {
            LegId = leg.Id,
            ContractNumber = leg.SourcePurchaseContract?.ContractNumber
                ?? "Contract #" + leg.SourcePurchaseContractId,
            ProductName = leg.Product?.Name ?? "Product #" + leg.ProductId,
            SourceTerminalName = leg.SourceTerminal?.Name ?? "Terminal #" + leg.SourceTerminalId,
            SourceTankCode = StorageTankDisplay.BuildOptional(leg.SourceStorageTank),
            QuantityMt = leg.QuantityMt,
            StatusText = leg.Status.ToString(),
            MovementId = leg.OutboundInventoryMovementId,
            Issue = issue,
            Status = status
        };

    private static (string Issue, string Status)? ResolveInventoryTransportLegIssue(InventoryTransportLeg leg)
    {
        if (leg.Status == InventoryTransportLegStatus.Cancelled
            && leg.OutboundInventoryMovementId.HasValue)
        {
            return ("Cancelled transport leg still has outbound movement.", "Warning");
        }

        if ((leg.Status == InventoryTransportLegStatus.Loaded
                || leg.Status == InventoryTransportLegStatus.InTransit)
            && !leg.OutboundInventoryMovementId.HasValue)
        {
            return ("Transport leg is loaded but has no outbound inventory movement.", "Needs Review");
        }

        if (leg.OutboundInventoryMovementId.HasValue && leg.OutboundInventoryMovement is null)
        {
            return ("Transport leg references missing outbound inventory movement.", "Needs Review");
        }

        var movement = leg.OutboundInventoryMovement;
        if (movement is null)
        {
            return null;
        }

        if (movement.QuantityMt != leg.QuantityMt)
        {
            return ("Transport leg quantity does not match outbound movement quantity.", "Needs Review");
        }

        if (movement.ProductId != leg.ProductId
            || movement.ContractId != leg.SourcePurchaseContractId
            || movement.TerminalId != leg.SourceTerminalId
            || movement.StorageTankId != leg.SourceStorageTankId)
        {
            return ("Outbound movement does not match transport leg source.", "Needs Review");
        }

        return null;
    }

    private static DirectSaleReconciliationItemViewModel ToDirectSaleReconciliationItem(
        LoadingReceiptAllocation allocation,
        string issue,
        decimal? ledgerAmountUsd = null)
    {
        var sale = allocation.SalesTransaction;
        return new DirectSaleReconciliationItemViewModel
        {
            AllocationId = allocation.Id,
            LoadingReceiptId = allocation.LoadingReceiptId,
            ContractNumber = allocation.SourcePurchaseContract?.ContractNumber
                ?? (allocation.SourcePurchaseContractId.HasValue ? "Contract #" + allocation.SourcePurchaseContractId : ""),
            CustomerName = sale?.Customer?.Name,
            SalesTransactionId = allocation.SalesTransactionId,
            InvoiceNumber = sale?.InvoiceNumber ?? allocation.ReferenceDocument,
            AllocationQuantityMt = allocation.QuantityMt,
            SaleQuantityMt = sale?.QuantityMt,
            SaleTotalUsd = sale?.TotalUsd,
            LedgerAmountUsd = ledgerAmountUsd,
            Issue = issue,
            Status = "Needs Review"
        };
    }

    private static EmployeeSalaryReconciliationItemViewModel ToEmployeeSalaryIssue(
        EmployeeSalaryTransaction transaction,
        string issue,
        string status = "Needs Review")
        => new()
        {
            TransactionId = transaction.Id,
            EmployeeId = transaction.EmployeeId,
            EmployeeCode = transaction.Employee?.EmployeeCode ?? "",
            EmployeeName = transaction.Employee?.FullName ?? "",
            TransactionDate = transaction.TransactionDate,
            TransactionTypeName = EmployeeSalaryTransactionTypeLabels.ToPersian(transaction.TransactionType),
            AmountUsd = transaction.AmountUsd,
            CashAccountId = transaction.CashAccountId,
            CashAccountName = transaction.CashAccount?.Name,
            PaymentTransactionId = transaction.PaymentTransactionId,
            LedgerEntryId = transaction.LedgerEntryId,
            LedgerAmountUsd = transaction.LedgerEntry?.AmountUsd,
            PaymentAmountUsd = transaction.PaymentTransaction?.AmountUsd,
            Reference = transaction.Reference,
            Issue = issue,
            Status = status
        };

    private static DirectDispatchReconciliationItemViewModel ToDirectDispatchReconciliationItem(
        LoadingReceiptAllocation allocation,
        string issue)
    {
        var activeDispatches = GetActiveDirectFromReceiptDispatches(allocation);
        var firstDispatch = activeDispatches.OrderBy(d => d.DispatchDate).ThenBy(d => d.Id).FirstOrDefault();
        var dispatchedQuantityMt = activeDispatches.Sum(d => d.LoadedQuantityMt);

        return new DirectDispatchReconciliationItemViewModel
        {
            AllocationId = allocation.Id,
            TruckDispatchId = firstDispatch?.Id,
            LoadingReceiptId = allocation.LoadingReceiptId,
            ContractNumber = allocation.SourcePurchaseContract?.ContractNumber
                ?? (allocation.SourcePurchaseContractId.HasValue ? "Contract #" + allocation.SourcePurchaseContractId : ""),
            TruckPlateNumber = firstDispatch?.Truck?.PlateNumber,
            DriverName = firstDispatch?.Driver?.FullName,
            DestinationName = ResolveDirectDispatchDestination(allocation, firstDispatch),
            SalesTransactionId = firstDispatch?.SalesTransactionId,
            InvoiceNumber = firstDispatch?.SalesTransaction?.InvoiceNumber,
            AllocationQuantityMt = allocation.QuantityMt,
            DispatchedQuantityMt = dispatchedQuantityMt,
            SaleQuantityMt = firstDispatch?.SalesTransaction?.QuantityMt,
            SaleTotalUsd = firstDispatch?.SalesTransaction?.TotalUsd,
            RemainingQuantityMt = allocation.QuantityMt - dispatchedQuantityMt,
            Issue = issue,
            Status = "Needs Review"
        };
    }

    private static DirectDispatchReconciliationItemViewModel ToDirectDispatchReconciliationItem(
        TruckDispatch dispatch,
        string issue,
        LoadingReceiptAllocation? allocation = null,
        decimal? ledgerAmountUsd = null)
    {
        var resolvedAllocation = allocation ?? dispatch.LoadingReceiptAllocation;
        var allocationDispatchedQuantityMt = resolvedAllocation is null
            ? dispatch.LoadedQuantityMt
            : GetActiveDirectFromReceiptDispatches(resolvedAllocation).Sum(d => d.LoadedQuantityMt);

        return new DirectDispatchReconciliationItemViewModel
        {
            AllocationId = dispatch.LoadingReceiptAllocationId,
            TruckDispatchId = dispatch.Id,
            LoadingReceiptId = resolvedAllocation?.LoadingReceiptId,
            ContractNumber = resolvedAllocation?.SourcePurchaseContract?.ContractNumber
                ?? dispatch.Contract?.ContractNumber
                ?? (dispatch.ContractId > 0 ? "Contract #" + dispatch.ContractId : ""),
            TruckPlateNumber = dispatch.Truck?.PlateNumber,
            DriverName = dispatch.Driver?.FullName,
            DestinationName = ResolveDirectDispatchDestination(resolvedAllocation, dispatch),
            SalesTransactionId = dispatch.SalesTransactionId,
            InvoiceNumber = dispatch.SalesTransaction?.InvoiceNumber,
            AllocationQuantityMt = resolvedAllocation?.QuantityMt,
            DispatchedQuantityMt = dispatch.LoadedQuantityMt,
            SaleQuantityMt = dispatch.SalesTransaction?.QuantityMt,
            SaleTotalUsd = dispatch.SalesTransaction?.TotalUsd,
            LedgerAmountUsd = ledgerAmountUsd,
            RemainingQuantityMt = resolvedAllocation is null ? null : resolvedAllocation.QuantityMt - allocationDispatchedQuantityMt,
            Issue = issue,
            Status = "Needs Review"
        };
    }

    private static List<TruckDispatch> GetActiveDirectFromReceiptDispatches(LoadingReceiptAllocation allocation)
        => allocation.DirectTruckDispatches
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt && d.Status != DispatchStatus.Cancelled)
            .ToList();

    private static bool HasDirectDispatchStatusMismatch(LoadingReceiptAllocation allocation)
    {
        var activeDispatches = GetActiveDirectFromReceiptDispatches(allocation);
        var dispatchedQuantityMt = activeDispatches.Sum(d => d.LoadedQuantityMt);

        return allocation.Status switch
        {
            LoadingReceiptAllocationStatus.Completed => !QuantitiesMatch(dispatchedQuantityMt, allocation.QuantityMt),
            LoadingReceiptAllocationStatus.InTransit => dispatchedQuantityMt <= 0m || dispatchedQuantityMt >= allocation.QuantityMt,
            LoadingReceiptAllocationStatus.TraceOnly => dispatchedQuantityMt > 0m,
            _ => false
        };
    }

    private static bool QuantitiesMatch(decimal left, decimal right)
        => decimal.Round(left, 4, MidpointRounding.AwayFromZero)
            == decimal.Round(right, 4, MidpointRounding.AwayFromZero);

    private static string? ResolveDirectDispatchDestination(
        LoadingReceiptAllocation? allocation,
        TruckDispatch? dispatch)
        => dispatch?.DestinationLocation?.Name
            ?? allocation?.DestinationName
            ?? allocation?.DestinationLocation?.Name
            ?? allocation?.DestinationReference;

    // ── In-transit / unfinished after LoadingReceipt (read-only) ───────────
    // این بخش هیچ side-effect ندارد: نه movement، نه ledger، نه stock تغییر
    // می‌دهد. فقط چهار حالت ناتمام را نمایش می‌دهد تا کاربر بداند کدام
    // مسیرها بعد از رسید بارگیری در «وسط راه» مانده‌اند.
    public async Task<IncompleteAfterReceiptViewModel> BuildIncompleteAfterReceiptAsync()
    {
        // 1) DirectFromReceipt dispatches that are loaded/in-transit but have
        //    neither a Sale nor a DeliveryReceipt nor an InventoryMovement
        //    تخلیه‌شده در مقصد. این یعنی موتر بار را گرفته اما هنوز معلوم نیست
        //    به مخزن تخلیه شده یا فروش شده.
        var directDispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.Contract)
            .Include(d => d.DestinationLocation)
            .Include(d => d.SalesTransaction)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.LoadingReceipt)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.SourcePurchaseContract)
            .Include(d => d.LoadingReceiptAllocation)
                .ThenInclude(a => a!.DestinationLocation)
            .Where(d => d.DispatchMode == TruckDispatchMode.DirectFromReceipt
                && d.Status != DispatchStatus.Cancelled)
            .OrderBy(d => d.DispatchDate)
            .ThenBy(d => d.Id)
            .ToListAsync();

        var dispatchIds = directDispatches.Select(d => d.Id).ToList();
        var dispatchIdsWithDelivery = dispatchIds.Count == 0
            ? new HashSet<int>()
            : (await _db.DeliveryReceipts
                .AsNoTracking()
                .Where(r => r.TruckDispatchId.HasValue && dispatchIds.Contains(r.TruckDispatchId.Value))
                .Select(r => r.TruckDispatchId!.Value)
                .ToListAsync()).ToHashSet();

        var unloadReferences = dispatchIds.Select(id => $"TRUCK-UNLOAD:{id}").ToList();
        var unloadReferenceMap = dispatchIds.ToDictionary(id => $"TRUCK-UNLOAD:{id}", id => id);
        var dispatchIdsWithUnloadMovement = dispatchIds.Count == 0
            ? new HashSet<int>()
            : (await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => m.ReferenceDocument != null
                    && unloadReferences.Contains(m.ReferenceDocument)
                    && m.Direction == MovementDirection.In)
                .Select(m => m.ReferenceDocument!)
                .ToListAsync())
                .Select(r => unloadReferenceMap.GetValueOrDefault(r))
                .Where(id => id > 0)
                .ToHashSet();

        var directDispatchesWithoutFinish = directDispatches
            .Where(d => !d.SalesTransactionId.HasValue
                && !dispatchIdsWithDelivery.Contains(d.Id)
                && !dispatchIdsWithUnloadMovement.Contains(d.Id)
                && (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit))
            .Select(d => new IncompleteAfterReceiptItemViewModel
            {
                PathType = "DirectDispatch",
                PathTypeText = "بارگیری مستقیم در موتر",
                ContractNumber = d.LoadingReceiptAllocation?.SourcePurchaseContract?.ContractNumber
                    ?? d.Contract?.ContractNumber
                    ?? (d.ContractId > 0 ? "Contract #" + d.ContractId : ""),
                LoadingRegisterId = d.LoadingReceiptAllocation?.LoadingReceipt?.LoadingRegisterId,
                LoadingReceiptId = d.LoadingReceiptAllocation?.LoadingReceiptId,
                AllocationId = d.LoadingReceiptAllocationId,
                TruckDispatchId = d.Id,
                Date = d.DispatchDate,
                QuantityMt = d.LoadedQuantityMt,
                TruckPlateNumber = d.Truck?.PlateNumber,
                DriverName = d.Driver?.FullName,
                DestinationName = d.DestinationLocation?.Name
                    ?? d.LoadingReceiptAllocation?.DestinationName
                    ?? d.LoadingReceiptAllocation?.DestinationLocation?.Name,
                CurrentStatus = d.Status.ToString(),
                Issue = "موتر از رسید/واگن بار گرفته اما هنوز نه فروش (Sale) و نه تخلیه در مخزن (Unload) ثبت شده است.",
                DetailsControllerName = "Dispatch",
                DetailsActionName = "Details",
                DetailsRouteId = d.Id
            })
            .ToList();

        // 2) Transfer allocations that are still InTransit. سیستم فعلی
        //    رسید مقصد ندارد، پس هر allocation TransferToOtherTerminal با
        //    وضعیت InTransit بدون مکانیزم خودکار تکمیل می‌ماند.
        var transfersInTransit = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.LoadingReceipt)
            .Include(a => a.DestinationTerminal)
            .Include(a => a.DestinationStorageTank)
            .Include(a => a.DestinationLocation)
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.TransferToOtherTerminal
                && a.Status == LoadingReceiptAllocationStatus.InTransit)
            .OrderBy(a => a.LoadingReceipt!.ReceiptDate)
            .ThenBy(a => a.Id)
            .Select(a => new IncompleteAfterReceiptItemViewModel
            {
                PathType = "Transfer",
                PathTypeText = "انتقال به ترمینال/شهر دیگر",
                ContractNumber = a.SourcePurchaseContract != null
                    ? a.SourcePurchaseContract.ContractNumber
                    : (a.SourcePurchaseContractId.HasValue ? "Contract #" + a.SourcePurchaseContractId : ""),
                LoadingRegisterId = a.LoadingReceipt != null ? a.LoadingReceipt.LoadingRegisterId : null,
                LoadingReceiptId = a.LoadingReceiptId,
                AllocationId = a.Id,
                Date = a.LoadingReceipt != null ? a.LoadingReceipt.ReceiptDate : (DateTime?)null,
                QuantityMt = a.QuantityMt,
                DestinationName = a.DestinationTerminal != null
                    ? (a.DestinationStorageTank != null
                        ? a.DestinationTerminal.Name + " / " + StorageTankDisplay.Build(a.DestinationStorageTank)
                        : a.DestinationTerminal.Name)
                    : (a.DestinationLocation != null ? a.DestinationLocation.Name : a.DestinationName),
                CurrentStatus = "InTransit",
                Issue = "این مقدار به ترمینال/شهر دیگر انتقال شده، اما هنوز رسید مقصد ثبت نشده است.",
                DetailsControllerName = "LoadingReceipts",
                DetailsActionName = "Details",
                DetailsRouteId = a.LoadingReceiptId
            })
            .ToListAsync();

        // 3) Conflict: dispatch هم Sale دارد هم DeliveryReceipt/Movement(In).
        //    این نباید اتفاق بیفتد چون یا فروش شده یا در مخزن تخلیه شده.
        var dispatchSaleAndDeliveryConflicts = directDispatches
            .Where(d => d.SalesTransactionId.HasValue
                && (dispatchIdsWithDelivery.Contains(d.Id) || dispatchIdsWithUnloadMovement.Contains(d.Id)))
            .Select(d =>
            {
                var hasDelivery = dispatchIdsWithDelivery.Contains(d.Id);
                var hasMovement = dispatchIdsWithUnloadMovement.Contains(d.Id);
                var conflictDetail = hasDelivery && hasMovement
                    ? "DeliveryReceipt و InventoryMovement(In)"
                    : hasDelivery ? "DeliveryReceipt" : "InventoryMovement(In)";
                return new IncompleteAfterReceiptItemViewModel
                {
                    PathType = "DispatchConflict",
                    PathTypeText = "تضاد فروش/تخلیه",
                    ContractNumber = d.LoadingReceiptAllocation?.SourcePurchaseContract?.ContractNumber
                        ?? d.Contract?.ContractNumber
                        ?? (d.ContractId > 0 ? "Contract #" + d.ContractId : ""),
                    LoadingRegisterId = d.LoadingReceiptAllocation?.LoadingReceipt?.LoadingRegisterId,
                    LoadingReceiptId = d.LoadingReceiptAllocation?.LoadingReceiptId,
                    AllocationId = d.LoadingReceiptAllocationId,
                    TruckDispatchId = d.Id,
                    SalesTransactionId = d.SalesTransactionId,
                    Date = d.DispatchDate,
                    QuantityMt = d.LoadedQuantityMt,
                    TruckPlateNumber = d.Truck?.PlateNumber,
                    DriverName = d.Driver?.FullName,
                    DestinationName = d.DestinationLocation?.Name
                        ?? d.LoadingReceiptAllocation?.DestinationName,
                    CurrentStatus = d.Status.ToString(),
                    Issue = $"این موتر هم Sale خورده و هم {conflictDetail} دارد؛ یکی از دو رویداد نباید رخ داده باشد.",
                    DetailsControllerName = "Dispatch",
                    DetailsActionName = "Details",
                    DetailsRouteId = d.Id
                };
            })
            .ToList();

        // 4) DirectDispatch با مقدار ناتمام: allocation که dispatch جزئی شده
        //    ولی هنوز کامل نیست (dispatched > 0 ولی dispatched < allocation).
        //    تفاوت با DirectDispatchQuantityMismatches موجود این است که اینجا
        //    Remaining را به‌صورت خاص نشان می‌دهیم تا کاربر بداند چقدر مانده.
        var directDispatchAllocationsForRemaining = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Include(a => a.SourcePurchaseContract)
            .Include(a => a.LoadingReceipt)
            .Include(a => a.DestinationLocation)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.Truck)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.Driver)
            .Include(a => a.DirectTruckDispatches)
                .ThenInclude(d => d.DestinationLocation)
            .AsSplitQuery()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                && a.Status != LoadingReceiptAllocationStatus.Cancelled
                && a.Status != LoadingReceiptAllocationStatus.Completed)
            .OrderBy(a => a.LoadingReceipt!.ReceiptDate)
            .ThenBy(a => a.Id)
            .ToListAsync();

        var directDispatchPartialRemaining = directDispatchAllocationsForRemaining
            .Select(a =>
            {
                var activeDispatches = GetActiveDirectFromReceiptDispatches(a);
                var dispatched = activeDispatches.Sum(d => d.LoadedQuantityMt);
                var remaining = a.QuantityMt - dispatched;
                return new { Allocation = a, Dispatched = dispatched, Remaining = remaining, ActiveDispatches = activeDispatches };
            })
            .Where(x => x.Dispatched > 0m && x.Remaining > 0m)
            .Select(x =>
            {
                var firstDispatch = x.ActiveDispatches
                    .OrderBy(d => d.DispatchDate)
                    .ThenBy(d => d.Id)
                    .FirstOrDefault();
                return new IncompleteAfterReceiptItemViewModel
                {
                    PathType = "DispatchPartial",
                    PathTypeText = "بارگیری مستقیم با مقدار ناتمام",
                    ContractNumber = x.Allocation.SourcePurchaseContract?.ContractNumber
                        ?? (x.Allocation.SourcePurchaseContractId.HasValue ? "Contract #" + x.Allocation.SourcePurchaseContractId : ""),
                    LoadingRegisterId = x.Allocation.LoadingReceipt?.LoadingRegisterId,
                    LoadingReceiptId = x.Allocation.LoadingReceiptId,
                    AllocationId = x.Allocation.Id,
                    TruckDispatchId = firstDispatch?.Id,
                    Date = x.Allocation.LoadingReceipt?.ReceiptDate,
                    QuantityMt = x.Allocation.QuantityMt,
                    DispatchedQuantityMt = x.Dispatched,
                    RemainingQuantityMt = x.Remaining,
                    TruckPlateNumber = firstDispatch?.Truck?.PlateNumber,
                    DriverName = firstDispatch?.Driver?.FullName,
                    DestinationName = firstDispatch?.DestinationLocation?.Name
                        ?? x.Allocation.DestinationName
                        ?? x.Allocation.DestinationLocation?.Name,
                    CurrentStatus = x.Allocation.Status.ToString(),
                    Issue = $"از {x.Allocation.QuantityMt:N3} MT تخصیص، {x.Dispatched:N3} MT دیسپچ شده و {x.Remaining:N3} MT باقی مانده است.",
                    DetailsControllerName = "LoadingReceipts",
                    DetailsActionName = "Details",
                    DetailsRouteId = x.Allocation.LoadingReceiptId
                };
            })
            .ToList();

        // 5) رسیدهای «ضایعات بعداً از تسویه مخزن» که هنوز تسویه نشده‌اند.
        //    این رسیدها موجودی را به مخزن وارد کرده‌اند ولی ضایعهٔ نهایی‌شان
        //    تا وقتی مخزن تسویه/خالی شود مشخص نیست. اگر موجودی دفتری همان
        //    (قرارداد منبع، مخزن) هنوز مثبت است یعنی هنوز تسویه نشده.
        var pendingTankSettlements = await BuildPendingTankSettlementsAsync();

        return new IncompleteAfterReceiptViewModel
        {
            DirectDispatchesWithoutFinish = directDispatchesWithoutFinish,
            TransfersInTransit = transfersInTransit,
            DispatchSaleAndDeliveryConflicts = dispatchSaleAndDeliveryConflicts,
            DirectDispatchPartialRemaining = directDispatchPartialRemaining,
            PendingTankSettlements = pendingTankSettlements
        };
    }

    private async Task<List<IncompleteAfterReceiptItemViewModel>> BuildPendingTankSettlementsAsync()
    {
        var deferredReceipts = await _db.LoadingReceipts
            .AsNoTracking()
            .Include(r => r.StorageTank)
            .Include(r => r.LoadingRegister)
                .ThenInclude(l => l!.Contract)
            .Where(r => r.LossMode == ReceiptLossMode.DeferredTankSettlement
                && r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                && r.StorageTankId != null
                && r.LoadingRegister != null)
            .OrderBy(r => r.ReceiptDate)
            .ThenBy(r => r.Id)
            .ToListAsync();

        if (deferredReceipts.Count == 0)
        {
            return [];
        }

        // موجودی دفتری به‌ازای (مخزن، قرارداد منبع) — با فالبک قرارداد از رسید→بارگیری.
        var tankIds = deferredReceipts.Select(r => r.StorageTankId!.Value).Distinct().ToList();
        var balanceByTankContract = (await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.StorageTankId != null && tankIds.Contains(m.StorageTankId!.Value))
            .Select(m => new
            {
                StorageTankId = m.StorageTankId!.Value,
                EffectiveContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                m.Direction,
                m.QuantityMt
            })
            .ToListAsync())
            .Where(m => m.EffectiveContractId.HasValue)
            .GroupBy(m => new { m.StorageTankId, ContractId = m.EffectiveContractId!.Value })
            .ToDictionary(
                g => (g.Key.StorageTankId, g.Key.ContractId),
                g => g.Sum(m =>
                    m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                        ? m.QuantityMt
                        : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                            ? -m.QuantityMt
                            : 0m));

        var items = new List<IncompleteAfterReceiptItemViewModel>();
        var seen = new HashSet<(int TankId, int ContractId)>();
        foreach (var receipt in deferredReceipts)
        {
            var tankId = receipt.StorageTankId!.Value;
            var contractId = receipt.LoadingRegister!.ContractId;
            if (!seen.Add((tankId, contractId)))
            {
                continue;
            }

            if (!balanceByTankContract.TryGetValue((tankId, contractId), out var balance) || balance <= 0m)
            {
                continue;
            }

            items.Add(new IncompleteAfterReceiptItemViewModel
            {
                PathType = "DeferredTankSettlement",
                PathTypeText = "در انتظار تسویه نهایی مخزن",
                ContractNumber = receipt.LoadingRegister.Contract?.ContractNumber
                    ?? ("Contract #" + contractId),
                LoadingRegisterId = receipt.LoadingRegisterId,
                LoadingReceiptId = receipt.Id,
                Date = receipt.ReceiptDate,
                QuantityMt = balance,
                DestinationName = StorageTankDisplay.BuildOptional(receipt.StorageTank),
                CurrentStatus = "PendingSettlement",
                Issue = $"این رسید با حالت «ضایعات بعداً از تسویه مخزن» ثبت شده و {balance:N3} MT آن هنوز در مخزن {StorageTankDisplay.BuildOptional(receipt.StorageTank)} است؛ برای قطعی‌شدن ضایعات، مخزن را تسویه کنید.",
                DetailsControllerName = "StorageTanks",
                DetailsActionName = "SettleFinal",
                DetailsRouteId = tankId
            });
        }

        return items;
    }

    public async Task<NonZeroBalancesViewModel> BuildNonZeroBalancesAsync()
    {
        // دفتر کل بزرگ‌ترین جدول سیستم است؛ برای مانده‌گیری فقط همان ۸ ستون لازم خوانده
        // می‌شود، نه کل رکورد با سه Include. تعداد query تغییر نکرده اما حجم داده و
        // materialization موجودیت‌ها حذف شده و نتیجه دقیقاً همان است.
        var ledgerEntries = await _db.LedgerEntries
            .AsNoTracking()
            .Select(l => new BalanceSourceRow(
                l.ContractId,
                l.CustomerId,
                l.SupplierId,
                l.Contract != null ? l.Contract.ContractNumber : null,
                l.Customer != null ? l.Customer.Name : null,
                l.Supplier != null ? l.Supplier.Name : null,
                l.Side,
                l.AmountUsd))
            .ToListAsync();

        return new NonZeroBalancesViewModel
        {
            ContractBalances = BuildBalanceRows(
                ledgerEntries.Where(l => l.ContractId.HasValue),
                "Contract",
                l => l.ContractId!.Value,
                l => l.ContractNumber ?? ("Contract #" + l.ContractId)),
            CustomerBalances = BuildBalanceRows(
                ledgerEntries.Where(l => l.CustomerId.HasValue),
                "Customer",
                l => l.CustomerId!.Value,
                l => l.CustomerName ?? ("Customer #" + l.CustomerId)),
            SupplierBalances = BuildBalanceRows(
                ledgerEntries.Where(l => l.SupplierId.HasValue),
                "Supplier",
                l => l.SupplierId!.Value,
                l => l.SupplierName ?? ("Supplier #" + l.SupplierId))
        };
    }

    /// <summary>فقط ستون‌های لازم برای مانده‌گیری دفتر کل.</summary>
    private sealed record BalanceSourceRow(
        int? ContractId,
        int? CustomerId,
        int? SupplierId,
        string? ContractNumber,
        string? CustomerName,
        string? SupplierName,
        LedgerSide Side,
        decimal AmountUsd);

    private static List<NonZeroBalanceItemViewModel> BuildBalanceRows(
        IEnumerable<BalanceSourceRow> entries,
        string entityType,
        Func<BalanceSourceRow, int> idSelector,
        Func<BalanceSourceRow, string> nameSelector)
    {
        return entries
            .GroupBy(idSelector)
            .Select(g =>
            {
                var first = g.First();
                var debit = g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd);
                var credit = g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd);
                return new NonZeroBalanceItemViewModel
                {
                    EntityId = g.Key,
                    EntityType = entityType,
                    Name = nameSelector(first),
                    DebitUsd = debit,
                    CreditUsd = credit,
                    Status = "Non-zero Balance"
                };
            })
            .Where(r => r.BalanceUsd != 0m)
            .OrderByDescending(r => Math.Abs(r.BalanceUsd))
            .ToList();
    }
}
