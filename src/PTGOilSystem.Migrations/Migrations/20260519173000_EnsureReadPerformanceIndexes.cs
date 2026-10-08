using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTGOilSystem.Web.Data;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260519173000_EnsureReadPerformanceIndexes")]
    public partial class EnsureReadPerformanceIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Contracts_Status_ContractDate" ON "Contracts" ("Status", "ContractDate");
                CREATE INDEX IF NOT EXISTS "IX_Contracts_ContractType_ContractDate" ON "Contracts" ("ContractType", "ContractDate");
                CREATE INDEX IF NOT EXISTS "IX_Contracts_Status_EndDate" ON "Contracts" ("Status", "EndDate");

                CREATE INDEX IF NOT EXISTS "IX_SalesTransactions_Active_SaleDate" ON "SalesTransactions" ("SaleDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_SalesTransactions_Active_ContractId_SaleDate" ON "SalesTransactions" ("ContractId", "SaleDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_SalesTransactions_Active_ShipmentId_SaleDate" ON "SalesTransactions" ("ShipmentId", "SaleDate") WHERE NOT "IsCancelled";

                CREATE INDEX IF NOT EXISTS "IX_ExpenseTransactions_Active_ExpenseDate" ON "ExpenseTransactions" ("ExpenseDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_ExpenseTransactions_Active_ContractId_ExpenseDate" ON "ExpenseTransactions" ("ContractId", "ExpenseDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_ExpenseTransactions_Active_ShipmentId_ExpenseDate" ON "ExpenseTransactions" ("ShipmentId", "ExpenseDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_ExpenseTransactions_Active_TruckDispatchId_ExpenseDate" ON "ExpenseTransactions" ("TruckDispatchId", "ExpenseDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_ExpenseTransactions_Active_TransportLegId_ExpenseDate" ON "ExpenseTransactions" ("TransportLegId", "ExpenseDate") WHERE NOT "IsCancelled";

                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_ContractId_PaymentDate" ON "PaymentTransactions" ("ContractId", "PaymentDate");
                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_ShipmentId_PaymentDate" ON "PaymentTransactions" ("ShipmentId", "PaymentDate");
                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_EmployeeId_PaymentDate" ON "PaymentTransactions" ("EmployeeId", "PaymentDate");
                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_TruckDispatchId_PaymentDate" ON "PaymentTransactions" ("TruckDispatchId", "PaymentDate");
                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_Direction_PaymentDate" ON "PaymentTransactions" ("Direction", "PaymentDate");
                CREATE INDEX IF NOT EXISTS "IX_PaymentTransactions_PaymentKind_PaymentDate" ON "PaymentTransactions" ("PaymentKind", "PaymentDate");

                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_ContractId_EntryDate" ON "LedgerEntries" ("ContractId", "EntryDate");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_CustomerId_EntryDate" ON "LedgerEntries" ("CustomerId", "EntryDate");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_SupplierId_EntryDate" ON "LedgerEntries" ("SupplierId", "EntryDate");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_EmployeeId_EntryDate" ON "LedgerEntries" ("EmployeeId", "EntryDate");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_ShipmentId_EntryDate" ON "LedgerEntries" ("ShipmentId", "EntryDate");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_SourceType_SourceId" ON "LedgerEntries" ("SourceType", "SourceId");
                CREATE INDEX IF NOT EXISTS "IX_LedgerEntries_SourceCurrencyCode_EntryDate" ON "LedgerEntries" ("SourceCurrencyCode", "EntryDate");

                CREATE INDEX IF NOT EXISTS "IX_LoadingRegisters_ContractId_LoadingDate" ON "LoadingRegisters" ("ContractId", "LoadingDate");
                CREATE INDEX IF NOT EXISTS "IX_LoadingRegisters_ProductId_LoadingDate" ON "LoadingRegisters" ("ProductId", "LoadingDate");
                CREATE INDEX IF NOT EXISTS "IX_LoadingRegisters_LoadingDate" ON "LoadingRegisters" ("LoadingDate");
                CREATE INDEX IF NOT EXISTS "IX_LoadingReceipts_ReceiptDate" ON "LoadingReceipts" ("ReceiptDate");
                CREATE INDEX IF NOT EXISTS "IX_LoadingReceipts_LoadingRegisterId_ReceiptDate" ON "LoadingReceipts" ("LoadingRegisterId", "ReceiptDate");
                CREATE INDEX IF NOT EXISTS "IX_LoadingReceiptAllocations_SourcePurchaseContractId_Status" ON "LoadingReceiptAllocations" ("SourcePurchaseContractId", "Status");
                CREATE INDEX IF NOT EXISTS "IX_LoadingReceiptAllocations_LoadingReceiptId" ON "LoadingReceiptAllocations" ("LoadingReceiptId");

                CREATE INDEX IF NOT EXISTS "IX_InventoryTransportLegs_SourcePurchaseContractId_LoadedDate" ON "InventoryTransportLegs" ("SourcePurchaseContractId", "LoadedDate");
                CREATE INDEX IF NOT EXISTS "IX_CustomsDeclarations_LoadingRegisterId" ON "CustomsDeclarations" ("LoadingRegisterId");
                CREATE INDEX IF NOT EXISTS "IX_CustomsDeclarations_TransportLegId" ON "CustomsDeclarations" ("TransportLegId");
                CREATE INDEX IF NOT EXISTS "IX_LossEvents_Active_ContractId_EventDate" ON "LossEvents" ("ContractId", "EventDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_LossEvents_LoadingRegisterId" ON "LossEvents" ("LoadingRegisterId");
                CREATE INDEX IF NOT EXISTS "IX_LossEvents_TransportLegId" ON "LossEvents" ("TransportLegId");

                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_Active_DispatchDate" ON "TruckDispatches" ("DispatchDate") WHERE "Status" <> 4;
                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_Active_ContractId_DispatchDate" ON "TruckDispatches" ("ContractId", "DispatchDate") WHERE "Status" <> 4;
                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_DispatchMode_Status" ON "TruckDispatches" ("DispatchMode", "Status");
                CREATE INDEX IF NOT EXISTS "IX_TruckDispatches_LoadingReceiptAllocationId_Status" ON "TruckDispatches" ("LoadingReceiptAllocationId", "Status");

                CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_ProductId_TerminalId_ContractId" ON "InventoryMovements" ("ProductId", "TerminalId", "ContractId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_ProductId_TerminalId_StorageTankId_ContractId" ON "InventoryMovements" ("ProductId", "TerminalId", "StorageTankId", "ContractId");
                CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_ContractId_MovementDate" ON "InventoryMovements" ("ContractId", "MovementDate");
                CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_TerminalId_MovementDate" ON "InventoryMovements" ("TerminalId", "MovementDate");

                CREATE INDEX IF NOT EXISTS "IX_EmployeeSalaryTransactions_EmployeeId_IsCancelled_TransactionDate" ON "EmployeeSalaryTransactions" ("EmployeeId", "IsCancelled", "TransactionDate");
                CREATE INDEX IF NOT EXISTS "IX_EmployeeSalaryTransactions_Active_TransactionType_Date" ON "EmployeeSalaryTransactions" ("TransactionType", "TransactionDate") WHERE NOT "IsCancelled";
                CREATE INDEX IF NOT EXISTS "IX_EmployeeSalaryTransactions_Active_CashAccountId_Date" ON "EmployeeSalaryTransactions" ("CashAccountId", "TransactionDate") WHERE NOT "IsCancelled";

                CREATE INDEX IF NOT EXISTS "IX_Customers_IsActive_Name" ON "Customers" ("IsActive", "Name");
                CREATE INDEX IF NOT EXISTS "IX_Suppliers_IsActive_Name" ON "Suppliers" ("IsActive", "Name");
                CREATE INDEX IF NOT EXISTS "IX_Drivers_IsActive_FullName" ON "Drivers" ("IsActive", "FullName");
                CREATE INDEX IF NOT EXISTS "IX_Employees_IsActive_FullName" ON "Employees" ("IsActive", "FullName");
                CREATE INDEX IF NOT EXISTS "IX_Products_IsActive_Code" ON "Products" ("IsActive", "Code");
                CREATE INDEX IF NOT EXISTS "IX_Terminals_IsActive_Code" ON "Terminals" ("IsActive", "Code");
                CREATE INDEX IF NOT EXISTS "IX_Trucks_IsActive_PlateNumber" ON "Trucks" ("IsActive", "PlateNumber");
                CREATE INDEX IF NOT EXISTS "IX_StorageTanks_IsActive_TankCode" ON "StorageTanks" ("IsActive", "TankCode");

                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_ActionAtUtc" ON "AuditLogs" ("ActionAtUtc");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Module" ON "AuditLogs" ("Module");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Action" ON "AuditLogs" ("Action");
                CREATE INDEX IF NOT EXISTS "IX_AuditLogs_IsSuccess_ActionAtUtc" ON "AuditLogs" ("IsSuccess", "ActionAtUtc");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This migration only ensures read-performance indexes exist.
            // Do not drop indexes here because some may have been created by
            // earlier migrations or manual database maintenance.
        }
    }
}
