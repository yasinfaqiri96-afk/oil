using System.Text;
using Npgsql;

var rawConnectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=ptg_oil_system;SSL Mode=Prefer;Trust Server Certificate=true";

var connectionString = BuildPostgresConnectionString(rawConnectionString);
var dryRun = IsTruthy(Environment.GetEnvironmentVariable("DRY_RUN"));

var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Users",
    "Roles",
    "__EFMigrationsHistory",

    // Base definitions (تعاریف پایه)
    "Products",
    "Currencies",
    "Units",
    "Locations",
    "Companies",
    "Suppliers",
    "Customers",
    "Partners",
    "ServiceProviders",
    "Terminals",
    "StorageTanks",
    "Vessels",
    "Trucks",
    "Wagons",
    "Drivers",
    "ExpenseTypes",
    "Accounts",
    "AccountingSettings",
    "FiscalYears",
    "FiscalPeriods",
};


foreach (var extra in (Environment.GetEnvironmentVariable("PRESERVE_TABLES") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    preserve.Add(extra);
}
Console.WriteLine("Using connection: " + MaskPassword(connectionString));
Console.WriteLine(dryRun ? "Mode: DRY_RUN" : "Mode: BACKUP_AND_TRUNCATE");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

var allTables = await GetPublicTablesAsync(connection);
var preservedTables = allTables
    .Where(table => preserve.Contains(table))
    .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
    .ToList();
var tablesToTruncate = allTables
    .Where(table => !preserve.Contains(table))
    .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine();
Console.WriteLine($"PRESERVE ({preservedTables.Count}): {string.Join(", ", preservedTables)}");
Console.WriteLine($"TRUNCATE ({tablesToTruncate.Count}): {string.Join(", ", tablesToTruncate)}");


var conflicts = await GetPreservedForeignKeysAsync(connection, preserve);
var blocking = conflicts.Where(conflict => !conflict.IsNullable).ToList();
var clearable = conflicts.Where(conflict => conflict.IsNullable).ToList();

if (conflicts.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("FK CONFLICTS (preserved table references a table scheduled for truncate):");
    foreach (var conflict in conflicts)
    {
        Console.WriteLine($"  {conflict.Table}.{conflict.Column} -> {conflict.Referenced}" +
                          (conflict.IsNullable ? " (will be set to NULL)" : " (NOT NULL - blocking)"));
    }
}

if (blocking.Count > 0)
{
    Console.WriteLine("Aborting: TRUNCATE would fail, and CASCADE would also empty preserved tables.");
    return 3;
}
var preservedRows = await CountRowsAsync(connection, preservedTables);
Console.WriteLine("PRESERVED ROWS: " + string.Join(", ", preservedRows.Select(item => $"{item.Key}={item.Value}")));

if (tablesToTruncate.Count == 0)
{
    Console.WriteLine("Nothing to truncate.");
    return 0;
}

if (dryRun)
{
    return 0;
}

var backupDirectory = Path.Combine("artifacts", $"db-clear-except-users-{DateTime.UtcNow:yyyyMMddHHmmss}");
Directory.CreateDirectory(backupDirectory);

Console.WriteLine();
Console.WriteLine($"Backup directory: {backupDirectory}");
await WriteManifestAsync(backupDirectory, preservedTables, tablesToTruncate);
await BackupTablesAsync(connection, backupDirectory, tablesToTruncate);

Console.WriteLine();
Console.WriteLine("Truncating non-user tables...");
await using (var transaction = await connection.BeginTransactionAsync())
{
    foreach (var conflict in clearable)
    {
        var updateSql = $"UPDATE public.{QuoteIdentifier(conflict.Table)} SET {QuoteIdentifier(conflict.Column)} = NULL WHERE {QuoteIdentifier(conflict.Column)} IS NOT NULL";
        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
        var affected = await updateCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"Cleared {conflict.Table}.{conflict.Column} on {affected} row(s).");
    }

    var droppedConstraints = new List<(string Table, string Name, string Definition)>();
    foreach (var conflict in clearable.DistinctBy(item => item.ConstraintName, StringComparer.OrdinalIgnoreCase))
    {
        const string defSql = """
            SELECT pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
            WHERE nsp.nspname = 'public' AND con.conname = @name
            """;

        await using var defCommand = new NpgsqlCommand(defSql, connection, transaction);
        defCommand.Parameters.AddWithValue("name", conflict.ConstraintName);
        var definition = (string?)await defCommand.ExecuteScalarAsync();
        if (definition is null)
        {
            continue;
        }

        droppedConstraints.Add((conflict.Table, conflict.ConstraintName, definition));
        var dropSql = $"ALTER TABLE public.{QuoteIdentifier(conflict.Table)} DROP CONSTRAINT {QuoteIdentifier(conflict.ConstraintName)}";
        await using var dropCommand = new NpgsqlCommand(dropSql, connection, transaction);
        await dropCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"Dropped FK {conflict.ConstraintName} on {conflict.Table} (will be restored).");
    }

    var tableList = string.Join(", ", tablesToTruncate.Select(table => $"public.{QuoteIdentifier(table)}"));
    var sql = $"TRUNCATE TABLE {tableList} RESTART IDENTITY";
    await using (var command = new NpgsqlCommand(sql, connection, transaction))
    {
        await command.ExecuteNonQueryAsync();
    }

    foreach (var constraint in droppedConstraints)
    {
        var addSql = $"ALTER TABLE public.{QuoteIdentifier(constraint.Table)} ADD CONSTRAINT {QuoteIdentifier(constraint.Name)} {constraint.Definition}";
        await using var addCommand = new NpgsqlCommand(addSql, connection, transaction);
        await addCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"Restored FK {constraint.Name} on {constraint.Table}.");
    }

    await transaction.CommitAsync();
}

var remainingRows = await CountRowsAsync(connection, tablesToTruncate);
var nonEmpty = remainingRows.Where(item => item.Value != 0).ToList();

Console.WriteLine("Truncate finished.");
Console.WriteLine(nonEmpty.Count == 0
    ? "Verification: all truncated tables are empty."
    : "Verification warning: non-empty tables: " + string.Join(", ", nonEmpty.Select(item => $"{item.Key}={item.Value}")));

return nonEmpty.Count == 0 ? 0 : 2;

static async Task<List<string>> GetPublicTablesAsync(NpgsqlConnection connection)
{
    const string sql = """
        SELECT tablename
        FROM pg_tables
        WHERE schemaname = 'public'
          AND tablename NOT LIKE 'pg_%'
        ORDER BY lower(tablename)
        """;

    var tables = new List<string>();
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        tables.Add(reader.GetString(0));
    }

    return tables;
}

static async Task BackupTablesAsync(NpgsqlConnection connection, string backupDirectory, IReadOnlyCollection<string> tables)
{
    foreach (var table in tables)
    {
        var filePath = Path.Combine(backupDirectory, table + ".csv");
        Console.WriteLine($"Backing up {table} -> {filePath}");
        await using var command = new NpgsqlCommand($"SELECT * FROM public.{QuoteIdentifier(table)}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        await writer.WriteLineAsync(string.Join(",", columns.Select(EscapeCsv)));

        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? string.Empty
                    : EscapeCsv(Convert.ToString(reader.GetValue(index)) ?? string.Empty);
            }

            await writer.WriteLineAsync(string.Join(",", values));
        }
    }
}

static async Task WriteManifestAsync(string backupDirectory, IReadOnlyList<string> preservedTables, IReadOnlyList<string> truncatedTables)
{
    var filePath = Path.Combine(backupDirectory, "_manifest.txt");
    var lines = new List<string>
    {
        "CreatedUtc=" + DateTime.UtcNow.ToString("O"),
        "Preserved=" + string.Join(", ", preservedTables),
        "Truncated=" + string.Join(", ", truncatedTables),
    };

    await File.WriteAllLinesAsync(filePath, lines, new UTF8Encoding(false));
}

static async Task<Dictionary<string, long>> CountRowsAsync(NpgsqlConnection connection, IReadOnlyCollection<string> tables)
{
    var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var table in tables)
    {
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{QuoteIdentifier(table)}", connection);
        counts[table] = (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    return counts;
}

static string QuoteIdentifier(string identifier)
    => "\"" + identifier.Replace("\"", "\"\"") + "\"";

static string EscapeCsv(string value)
{
    if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
    {
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    return value;
}

static string BuildPostgresConnectionString(string raw)
{
    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        return $"Host={uri.Host};Port={port};Username={username};Password={password};Database={database};SSL Mode=Prefer;Trust Server Certificate=true";
    }

    return raw;
}

static string MaskPassword(string connectionString)
{
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    for (var index = 0; index < parts.Length; index++)
    {
        if (parts[index].StartsWith("Password=", StringComparison.OrdinalIgnoreCase) ||
            parts[index].StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = parts[index].IndexOf('=');
            if (separatorIndex >= 0)
            {
                parts[index] = parts[index][..(separatorIndex + 1)] + "****";
            }
        }
    }

    return string.Join(";", parts) + ";";
}

static bool IsTruthy(string? value)
    => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

static async Task<List<ForeignKeyConflict>> GetPreservedForeignKeysAsync(NpgsqlConnection connection, HashSet<string> preserve)
{
    const string sql = """
        SELECT tc.table_name AS referencing, kcu.column_name AS referencing_column,
               ccu.table_name AS referenced, col.is_nullable, tc.constraint_name
        FROM information_schema.table_constraints AS tc
        JOIN information_schema.key_column_usage AS kcu
          ON kcu.constraint_name = tc.constraint_name
         AND kcu.constraint_schema = tc.constraint_schema
        JOIN information_schema.constraint_column_usage AS ccu
          ON ccu.constraint_name = tc.constraint_name
         AND ccu.constraint_schema = tc.constraint_schema
        JOIN information_schema.columns AS col
          ON col.table_schema = tc.table_schema
         AND col.table_name = tc.table_name
         AND col.column_name = kcu.column_name
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND tc.table_schema = 'public'
        """;

    var conflicts = new List<ForeignKeyConflict>();
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var referencing = reader.GetString(0);
        var column = reader.GetString(1);
        var referenced = reader.GetString(2);
        var isNullable = string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);
        var constraintName = reader.GetString(4);
        if (preserve.Contains(referencing) && !preserve.Contains(referenced))
        {
            conflicts.Add(new ForeignKeyConflict(referencing, column, referenced, isNullable, constraintName));
        }
    }

    return conflicts
        .DistinctBy(item => (item.Table.ToLowerInvariant(), item.Column.ToLowerInvariant()))
        .OrderBy(item => item.Table, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Column, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

internal sealed record ForeignKeyConflict(string Table, string Column, string Referenced, bool IsNullable, string ConstraintName);
