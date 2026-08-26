using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartnerSettlementImport;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;

Console.OutputEncoding = Encoding.UTF8;

var file = ArgValue("--file");
var creditPayerName = ArgValue("--credit-payer");
var creditReceiverName = ArgValue("--credit-receiver");
var referencePrefix = ArgValue("--reference-prefix");
var apply = Environment.GetCommandLineArgs().Contains("--apply", StringComparer.OrdinalIgnoreCase);

if (file is null || creditPayerName is null || creditReceiverName is null || referencePrefix is null)
{
    Console.Error.WriteLine(
        "usage: --file <xlsx> --credit-payer <name> --credit-receiver <name> --reference-prefix <prefix> [--apply]");
    return 2;
}

var connection = Environment.GetEnvironmentVariable("DATABASE_URL")
                 ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("connection string not set (DATABASE_URL / ConnectionStrings__DefaultConnection).");
    return 2;
}

var services = new ServiceCollection();
services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(connection));
services.AddScoped<IPurchaseAggregationService, PurchaseAggregationService>();
services.AddScoped<IPartnershipStatementService, PartnershipStatementService>();
services.AddScoped<IAuditService>(sp => new AuditService(sp.GetRequiredService<ApplicationDbContext>()));

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
var statements = scope.ServiceProvider.GetRequiredService<IPartnershipStatementService>();
var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

var creditPayer = await FindPartnerAsync(db, creditPayerName);
var creditReceiver = await FindPartnerAsync(db, creditReceiverName);
if (creditPayer is null || creditReceiver is null)
{
    Console.Error.WriteLine("partner not found by exact name. aborting.");
    return 2;
}

var dbConnection = db.Database.GetDbConnection();
Console.WriteLine($"DB      : {dbConnection.Database} @ {dbConnection.DataSource}");
Console.WriteLine($"Partners: [{creditPayer.Id}] {creditPayer.Name}  |  [{creditReceiver.Id}] {creditReceiver.Name}");
Console.WriteLine($"Mode    : {(apply ? "APPLY" : "DRY RUN")}");
Console.WriteLine();

List<SettlementSourceRow> sourceRows;
await using (var sourceStream = File.OpenRead(file))
{
    sourceRows = SettlementSourceReader.Read(sourceStream).ToList();
}

var planned = SettlementImporter.Plan(sourceRows, creditPayer, creditReceiver, referencePrefix);
var evaluation = await SettlementImporter.EvaluateAsync(db, planned);

Console.WriteLine("=== SOURCE ROWS ===");
foreach (var item in planned)
{
    Console.WriteLine(
        $"{item.Source.RowNumber,3} | {item.Source.JalaliDate,-11} | {item.Source.SettlementDate:yyyy-MM-dd} | " +
        $"{item.FromPartnerId} -> {item.ToPartnerId} | {item.AmountUsd,20} | {item.Reference,-22} | " +
        $"{evaluation.Statuses[item.Reference],-8} | {item.Description}");
}

var creditRaw = sourceRows.Where(r => r.Column == SourceColumn.TCredit).Sum(r => r.Amount);
var debitRaw = sourceRows.Where(r => r.Column == SourceColumn.TDebit).Sum(r => r.Amount);
var creditStored = planned.Where(p => p.Source.Column == SourceColumn.TCredit).Sum(p => p.AmountUsd);
var debitStored = planned.Where(p => p.Source.Column == SourceColumn.TDebit).Sum(p => p.AmountUsd);

Console.WriteLine();
Console.WriteLine("=== SOURCE TOTALS ===");
Console.WriteLine($"rows parsed : {sourceRows.Count}");
Console.WriteLine($"[{creditPayer.Id}] -> [{creditReceiver.Id}] (T-Credit) raw    : {creditRaw}");
Console.WriteLine($"[{creditPayer.Id}] -> [{creditReceiver.Id}] (T-Credit) stored : {creditStored}");
Console.WriteLine($"[{creditReceiver.Id}] -> [{creditPayer.Id}] (T-Debit) raw     : {debitRaw}");
Console.WriteLine($"[{creditReceiver.Id}] -> [{creditPayer.Id}] (T-Debit) stored  : {debitStored}");
Console.WriteLine($"NET raw     : {creditRaw - debitRaw}");
Console.WriteLine($"NET stored  : {creditStored - debitStored}");
Console.WriteLine(
    $"NEW={evaluation.Statuses.Values.Count(v => v == PlannedStatus.New)} " +
    $"EXISTS={evaluation.Statuses.Values.Count(v => v == PlannedStatus.Exists)} " +
    $"CONFLICT={evaluation.Statuses.Values.Count(v => v == PlannedStatus.Conflict)}");
Console.WriteLine();

Console.WriteLine("=== STATEMENT BEFORE ===");
await PrintStatementAsync(statements, creditPayer.Id, creditReceiver.Id);

if (evaluation.Conflicts.Count > 0)
{
    Console.WriteLine();
    Console.Error.WriteLine("=== CONFLICTS — NOTHING WRITTEN ===");
    foreach (var line in evaluation.Conflicts)
    {
        Console.Error.WriteLine(line);
    }

    return 3;
}

if (!apply)
{
    Console.WriteLine();
    Console.WriteLine("dry run only. no rows written.");
    return 0;
}

var inserted = await SettlementImporter.ApplyAsync(
    db,
    audit,
    planned,
    evaluation,
    s => Console.WriteLine($"inserted {s.Reference} id={s.Id} usd={s.AmountUsd}"));

Console.WriteLine();
Console.WriteLine($"=== APPLIED: {inserted} row(s) inserted ===");
Console.WriteLine();
Console.WriteLine("=== STATEMENT AFTER ===");
await PrintStatementAsync(statements, creditPayer.Id, creditReceiver.Id);
return 0;

static string? ArgValue(string name)
{
    var all = Environment.GetCommandLineArgs();
    for (var i = 0; i < all.Length - 1; i++)
    {
        if (string.Equals(all[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return all[i + 1];
        }
    }

    return null;
}

static async Task<Partner?> FindPartnerAsync(ApplicationDbContext db, string name)
{
    var trimmed = name.Trim();
    var matches = await db.Partners.AsNoTracking().Where(p => p.Name == trimmed).ToListAsync();
    if (matches.Count == 1)
    {
        return matches[0];
    }

    Console.Error.WriteLine("partner lookup returned " + matches.Count + " exact matches for the given name.");
    return null;
}

static async Task PrintStatementAsync(IPartnershipStatementService statements, int partnerAId, int partnerBId)
{
    var statement = await statements.BuildAsync(partnerAId, partnerBId);
    if (statement is null)
    {
        Console.WriteLine("statement unavailable.");
        return;
    }

    foreach (var total in statement.Totals)
    {
        Console.WriteLine(
            $"[{total.PartnerId}] {total.PartnerName}: Funding={total.FundingUsd} ProceedsHeld={total.ProceedsHeldUsd} " +
            $"ProfitShare={total.ProfitShareUsd} SettlementsPaid={total.SettlementsPaidUsd} " +
            $"SettlementsReceived={total.SettlementsReceivedUsd} NetPosition={total.NetPositionUsd}");
    }

    Console.WriteLine($"Debtor              : {statement.DebtorPartnerName ?? "-"}");
    Console.WriteLine($"Creditor            : {statement.CreditorPartnerName ?? "-"}");
    Console.WriteLine($"AmountDueUsd        : {statement.AmountDueUsd}");
    Console.WriteLine($"CreditorClaimUsd    : {statement.CreditorClaimUsd}");
    Console.WriteLine($"UnreconciledResidual: {statement.UnreconciledResidualUsd}");
    Console.WriteLine($"Settlement rows     : {statement.Settlements.Count}");

    foreach (var partnerId in new[] { partnerAId, partnerBId })
    {
        var profile = await statements.BuildForPartnerAsync(partnerId);
        if (profile is null)
        {
            continue;
        }

        Console.WriteLine(
            $"PROFILE [{profile.PartnerId}] {profile.PartnerName}: Net={profile.NetPositionUsd} " +
            $"Direction={profile.Direction} Amount={profile.AmountUsd} Entries={profile.Entries.Count} " +
            $"SettlementsPaid={profile.SettlementsPaidUsd} SettlementsReceived={profile.SettlementsReceivedUsd}");
    }
}
