# Zuri contract summary reporting repair

This isolated ASP.NET Core Razor application part is compiled against the exact installed
PTGOilSystem.Web.dll, not a rebuild of the incomplete GitHub working tree. The original
application DLL and every existing DLL remain byte-for-byte unchanged. The hosting startup
registers a read-only reporting service and result filter limited to purchase contract
journey detail pages without subcontracts. No new HTTP routes or financial writes are added.

The view source matches the installed PDB document hash and is copied to a unique Razor
path. Shared views and all original controllers remain in the installed application.

## Corrected report behavior

- Company operating expenses come from the installed economics service's individual
  expense components. Customs, cancellations and cost responsibility use its existing
  policy. Separately displayed physical loss is not counted again as an expense.
- Driver recovery is the recorded shortage charge, capped at the actual gross-to-net
  freight reduction. Already net freight remains net; recovery is not added again to profit.
- When the full priced purchase quantity is exhausted by sales plus physical losses,
  with zero inventory and no pending tank settlement, profit deducts the full purchase
  value and net expenses. Other contracts retain the installed profit calculation.
- Saleable remaining quantity subtracts registered physical losses.
- Transport overview includes all active receipts rather than only the latest receipt.
- Loss presentation distinguishes gross shortage value, driver freight recovery,
  and the remaining company-borne shortage.

For P-002: revenue 296250, full purchase 200000, net operating expenses 4089,
physical shortage 2.5 MT / 2500 USD, recovery 700, company shortage 1800,
completed profit 92161 USD and margin 31.11%.

## Validation

13 standalone accounting regression checks pass. All nine journey tabs return HTTP 200
using an isolated restored PostgreSQL database and a test-only authentication assembly.
Seven financial/inventory table content fingerprints remain identical before/after tests.
The original app DLL hash is 84b015bee035074f8294225e4a3c812f819890e0da7299adc2c98f8735d9bcb5.

The test authentication assembly is only deployed to the isolated loopback test instance,
checks for the dedicated test database, and must never be copied into a production release.

Build using the installed app directory mounted read-only at /runtime:
docker run --rm --network=none --memory=2g --cpus=2 \
  -v "$PWD":/src -v /opt/novatech/zuri/current:/runtime:ro -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release --ignore-failed-sources

Deployment copies only PTG.ContractReportingRepair.dll and its PDB into a copy of the
current release, adds that single project runtime entry to the existing app deps manifest,
and sets ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=PTG.ContractReportingRepair for Zuri alone.

## Limits

This repairs the contract journey reporting layer. Shared company P&L, partnership
settlement calculations and existing ledger entries remain unchanged. In particular,
the previously missing direct-sale COGS journal warning is retained in the finance tab;
this report repair does not fabricate inventory movements or journal entries.
Mixed-contract transport attribution keeps original overview values and does not
apply completed-profit reconciliation. Original transport row details remain unchanged.

Remove the hosting startup environment entry and switch back to the saved original release
to roll back. Backup and rollback scripts are kept in the server's P002 backup directory.

## Direct transport sale COGS repair (2026-10-07)

The installed ISalesAccountingAdapter is decorated, while its revenue, advances and inventory-sale logic remain unchanged. An active DirectSale receipt with no outbound stock movement and only direct LoadingRegister allocations is valued from the active, posted purchase journal of those loadings. The existing posting service enforces account ownership, currency and fiscal-period rules. COGS debits 5100 and credits 1310, not tank inventory. Journal and active SalesCostConsumption commit together under sale-row/event locks.

SalesCostConsumption has a required int TerminalId but no terminal FK. This extension reserves 0 for an in-transit cost snapshot; it does NOT create a Terminal, InventoryMovement or InventoryAverageCost pool. The marker and receipt/loading provenance are retained in the canonical journal. The decorator recognises this marker on reversal, reverses the exact journal, marks the snapshot reversed, and never calls valuation.ReturnAsync. Other cost snapshots keep their existing terminal IDs and original adapter.

Scope: provable, single-contract purchase-loading sources such as P002/INV003. Mixed-company, tank-origin, transferred-parent and other unprovable sources keep their warning rather than receive a guessed cost. Canonical ProfitAndLossService and reconciliation need no changes: they already read active SalesCostConsumption. Expenses are not capitalised or posted again. The finance variance now compares the displayed operational net and realised gross amounts and explains the expenses/losses difference.

Deployment does not rebuild the main assembly from incomplete sources. A Mono.Cecil deployment tool changes exactly one existing AddScoped generic argument in Program to this decorator and regenerates its portable PDB. Semantic IL hashes verify every method: exactly one method has exactly that registration change, and embedded resource hashes remain identical. All other executable DLLs remain byte-for-byte unchanged; the complete original release is retained. Test authentication/controller are ONLY in the loopback test runtime and the one-shot maintenance assembly is NOT copied into the public release. Production is backfilled only for reviewed INV003 (98MT, USD98000); no global backfill.

Rollback after posting: do not restore an older adapter while any active TerminalId=0 snapshot remains; that adapter would incorrectly return cost to a tank pool. First reverse affected direct COGS through this decorator and verify no active in-transit snapshot, or retain the decorator. The earlier reporting-only rollback is not valid after this accounting repair.

The HostingStartup callback runs before Program and cannot itself override the final adapter registration. Hence the registration-only patch is necessary on this installed build. No framework-private reflection, alternate service-provider factory or request-service interception is used.

Validation on the restored test database: 14 in-process checks passed through the actual application service provider, including canonical P&L, repeated posting, exact reversal, repeated reversal, unchanged inventory movements/pools and rollback. Registration verification covered 111,759 methods; only the AddScoped type argument in Program changed.
