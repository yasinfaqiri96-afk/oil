# پوشش اولیهٔ ممیزی طراحی — ۲۰۲۶-۱۰-۲۴

این جدول snapshot قبل از اصلاحات است؛ گواهی مشاهدهٔ تمام صفحات در مرورگر نیست. وضعیت اصلاحات در گزارش اجرای یکپارچه‌سازی ثبت می‌شود.

تمام ۴۶۹ فایل در ممیزی اولیه فهرست‌برداری و اسکن شدند. بررسی هدفمند یعنی خواندن محدودهٔ مرتبط کد. مصرف غیرمستقیم کامپوننت از طریق shell در ستون reference مستقیم شمرده نشده است. نبود reference مستقیم، یافتهٔ نقص نیست.

| پوشه | کل Razor | View غیرpartial | بررسی هدفمند | header مستقیم | filter مستقیم | RTL ثابت در ممیزی اولیه | مرورگر صفحات واقعی |
|---|---:|---:|---:|---:|---:|---:|---|
| (root) | 2 | 0 | 2 | 0 | 0 | 0 | انجام نشد |
| AccountStatements | 4 | 4 | 0 | 4 | 1 | 3 | انجام نشد |
| Attendance | 2 | 2 | 0 | 2 | 0 | 0 | انجام نشد |
| AuditLogs | 2 | 2 | 0 | 1 | 1 | 0 | انجام نشد |
| Auth | 3 | 3 | 0 | 1 | 0 | 0 | انجام نشد |
| BackupRestore | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Backups | 2 | 1 | 0 | 0 | 0 | 0 | انجام نشد |
| Balance | 3 | 3 | 0 | 3 | 3 | 0 | انجام نشد |
| CashAccounts | 5 | 4 | 0 | 2 | 1 | 1 | انجام نشد |
| ChartOfAccounts | 4 | 4 | 0 | 4 | 0 | 0 | انجام نشد |
| ClosingChecklist | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| CommodityLoans | 5 | 5 | 0 | 5 | 0 | 0 | انجام نشد |
| Companies | 5 | 4 | 0 | 3 | 1 | 1 | انجام نشد |
| ContractAmendments | 2 | 2 | 0 | 1 | 0 | 1 | انجام نشد |
| ContractBalanceTransfers | 3 | 3 | 0 | 3 | 1 | 2 | انجام نشد |
| ContractJourney | 10 | 2 | 0 | 1 | 0 | 0 | انجام نشد |
| Contracts | 6 | 6 | 1 | 5 | 1 | 0 | انجام نشد |
| Currencies | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| Custody | 13 | 12 | 0 | 12 | 0 | 0 | انجام نشد |
| CustomerToCustomerPayments | 5 | 4 | 3 | 2 | 1 | 1 | انجام نشد |
| Customers | 5 | 4 | 0 | 3 | 1 | 1 | انجام نشد |
| CustomsDeclarations | 5 | 5 | 0 | 5 | 1 | 1 | انجام نشد |
| CustomsPermitTurnover | 1 | 1 | 0 | 1 | 1 | 0 | انجام نشد |
| DailyFxRates | 5 | 4 | 0 | 4 | 1 | 0 | انجام نشد |
| Dispatch | 7 | 6 | 1 | 4 | 1 | 1 | انجام نشد |
| Drivers | 5 | 4 | 0 | 2 | 1 | 1 | انجام نشد |
| EmployeeLoans | 3 | 3 | 0 | 3 | 0 | 0 | انجام نشد |
| Employees | 8 | 7 | 0 | 7 | 1 | 1 | انجام نشد |
| EmploymentContracts | 3 | 3 | 0 | 3 | 1 | 0 | انجام نشد |
| ExpenseRules | 3 | 3 | 0 | 3 | 1 | 0 | انجام نشد |
| ExpenseTypes | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| Expenses | 9 | 9 | 1 | 5 | 1 | 3 | انجام نشد |
| FinalClose | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Finance | 1 | 1 | 0 | 0 | 0 | 0 | انجام نشد |
| FiscalYears | 2 | 2 | 0 | 2 | 0 | 0 | انجام نشد |
| FormDrafts | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Home | 4 | 2 | 1 | 0 | 0 | 0 | انجام نشد |
| HrDashboard | 1 | 1 | 1 | 1 | 0 | 0 | انجام نشد |
| HrOrganization | 3 | 3 | 0 | 3 | 0 | 0 | انجام نشد |
| HrReports | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| HrSettings | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Inventory | 4 | 4 | 0 | 4 | 2 | 0 | انجام نشد |
| InventoryReports | 1 | 1 | 0 | 1 | 1 | 1 | انجام نشد |
| InventoryTransportLegs | 17 | 14 | 0 | 10 | 1 | 6 | انجام نشد |
| InventoryTransportReceipts | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Invoices | 1 | 1 | 0 | 0 | 0 | 0 | انجام نشد |
| Leave | 2 | 2 | 0 | 2 | 1 | 0 | انجام نشد |
| Ledger | 2 | 2 | 0 | 2 | 1 | 1 | انجام نشد |
| Loading | 10 | 7 | 1 | 6 | 1 | 2 | انجام نشد |
| LoadingReceipts | 5 | 4 | 1 | 4 | 1 | 1 | انجام نشد |
| Locations | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| LossEvents | 4 | 4 | 0 | 4 | 1 | 1 | انجام نشد |
| OfflineSync | 2 | 2 | 0 | 2 | 0 | 0 | انجام نشد |
| OperationalAssets | 7 | 6 | 0 | 6 | 1 | 0 | انجام نشد |
| OperationalPeriodLocks | 1 | 1 | 0 | 1 | 0 | 1 | انجام نشد |
| Partners | 5 | 4 | 0 | 3 | 1 | 1 | انجام نشد |
| PartnershipStatement | 2 | 1 | 0 | 1 | 0 | 1 | انجام نشد |
| PartySettlements | 4 | 4 | 0 | 4 | 1 | 0 | انجام نشد |
| PartyStatements | 4 | 1 | 0 | 0 | 1 | 3 | انجام نشد |
| Payments | 5 | 5 | 0 | 5 | 1 | 4 | انجام نشد |
| Payroll | 2 | 2 | 0 | 2 | 0 | 0 | انجام نشد |
| PeriodActivity | 3 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| PlattsRates | 3 | 1 | 0 | 0 | 0 | 0 | انجام نشد |
| Products | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| QualityInspections | 5 | 4 | 0 | 4 | 1 | 3 | انجام نشد |
| Reconciliation | 13 | 10 | 0 | 10 | 0 | 10 | انجام نشد |
| ReopenFiscalYear | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Reports | 22 | 20 | 1 | 20 | 16 | 1 | انجام نشد |
| Roles | 5 | 4 | 0 | 4 | 1 | 0 | انجام نشد |
| Sales | 13 | 12 | 0 | 8 | 1 | 4 | انجام نشد |
| SarrafSettlements | 3 | 3 | 0 | 3 | 1 | 2 | انجام نشد |
| Sarrafs | 5 | 4 | 0 | 4 | 1 | 1 | انجام نشد |
| ServiceProviders | 5 | 4 | 0 | 3 | 1 | 1 | انجام نشد |
| Shared | 72 | 4 | 9 | 3 | 1 | 3 | انجام نشد |
| ShipmentContracts | 2 | 2 | 0 | 0 | 0 | 0 | انجام نشد |
| ShipmentPnl | 2 | 2 | 0 | 2 | 0 | 1 | انجام نشد |
| Shipments | 3 | 2 | 0 | 2 | 0 | 0 | انجام نشد |
| StorageTanks | 7 | 5 | 0 | 2 | 1 | 0 | انجام نشد |
| SupplierBalanceTransfers | 2 | 2 | 0 | 2 | 1 | 2 | انجام نشد |
| Suppliers | 7 | 4 | 0 | 3 | 1 | 1 | انجام نشد |
| SystemSettings | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| Terminals | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| ThreeWaySettlement | 2 | 2 | 1 | 2 | 0 | 2 | انجام نشد |
| Transports | 9 | 8 | 0 | 8 | 0 | 8 | انجام نشد |
| TrialClose | 1 | 1 | 0 | 1 | 0 | 0 | انجام نشد |
| TruckSettlements | 3 | 3 | 0 | 0 | 2 | 2 | انجام نشد |
| Trucks | 5 | 4 | 0 | 2 | 1 | 1 | انجام نشد |
| Units | 5 | 4 | 0 | 2 | 1 | 0 | انجام نشد |
| Users | 6 | 5 | 0 | 5 | 1 | 0 | انجام نشد |
| Vessels | 5 | 4 | 0 | 2 | 1 | 1 | انجام نشد |
| ViaSarrafPayments | 3 | 3 | 0 | 3 | 0 | 3 | انجام نشد |
| Wagons | 5 | 4 | 0 | 2 | 1 | 1 | انجام نشد |

برای شواهد اولیه به [گزارش ممیزی](ui-design-audit-2026-10-24.md) و برای نتیجهٔ اصلاحات به [گزارش اجرا](ui-design-unification-2026-10-24.md) مراجعه شود.
