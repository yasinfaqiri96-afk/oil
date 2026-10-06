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
