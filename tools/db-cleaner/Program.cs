using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace DbCleaner;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("DATABASE_URL")
                      ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                      ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=ptg_oil_system;SSL Mode=Prefer;Trust Server Certificate=true";

            var connString = BuildPostgresConnectionString(raw);

            Console.WriteLine("Using connection: " + MaskPassword(connString));

            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();

            // Preserve = users/auth only. Everything else (master data, contracts,
            // accounting setup, transactions) is truncated.
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // --- users / auth ---
                "users",
                "roles",
                "permissions",
                "rolepermissions",
                "userroles",
                "__efmigrationshistory",
            };

            var tables = new List<string>();
            await using (var cmd = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename NOT LIKE 'pg_%'", conn))
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                {
                    var t = rdr.GetString(0);
                    if (!exclude.Contains(t)) tables.Add(t);
                }
            }

            var allPublic = new List<string>();
            await using (var cmd = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename NOT LIKE 'pg_%'", conn))
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync()) allPublic.Add(rdr.GetString(0));
            }
            var preserved = allPublic.Where(t => exclude.Contains(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            var unmatchedExcludes = exclude.Where(e => !allPublic.Any(t => string.Equals(t, e, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t).ToList();

            Console.WriteLine();
            Console.WriteLine($"PRESERVE ({preserved.Count}): " + string.Join(", ", preserved));
            Console.WriteLine();
            Console.WriteLine($"TRUNCATE ({tables.Count}): " + string.Join(", ", tables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)));
            if (unmatchedExcludes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("WARNING - keep-list names with NO matching table (check spelling): " + string.Join(", ", unmatchedExcludes));
            }

            if (tables.Count == 0)
            {
                Console.WriteLine("No tables found to truncate.");
                return 0;
            }

            var dryRun = string.Equals(Environment.GetEnvironmentVariable("DRY_RUN"), "1", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Environment.GetEnvironmentVariable("DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase);
            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("DRY_RUN=1 -> no backup, no truncate. Exiting.");
                return 0;
            }

            var backupDir = Path.Combine("artifacts", $"db-backup-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(backupDir);
            Console.WriteLine($"Backing up {tables.Count} tables to: {backupDir}");

            foreach (var table in tables)
            {
                var file = Path.Combine(backupDir, table + ".csv");
                Console.WriteLine($"Backing up {table} -> {file}");
                await using (var cmd = new NpgsqlCommand($"SELECT * FROM public.\"{table}\"", conn))
                await using (var rdr = await cmd.ExecuteReaderAsync())
                await using (var writer = new StreamWriter(file))
                {
                    var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => rdr.GetName(i)).ToArray();
                    await writer.WriteLineAsync(string.Join(",", cols.Select(EscapeCsv)));
                    while (await rdr.ReadAsync())
                    {
                        var fields = new string[rdr.FieldCount];
                        for (int i = 0; i < rdr.FieldCount; i++)
                        {
                            if (rdr.IsDBNull(i)) fields[i] = "";
                            else
                            {
                                var val = rdr.GetValue(i);
                                fields[i] = EscapeCsv(val?.ToString() ?? "");
                            }
                        }
                        await writer.WriteLineAsync(string.Join(",", fields));
                    }
                }
            }

            Console.WriteLine("Backups complete. Proceeding to truncate tables...");

            await using (var tx = await conn.BeginTransactionAsync())
            {
                try
                {
                    var tableList = string.Join(", ", tables.Select(t => $"public.{QuoteIdentifier(t)}"));
                    var sql = $"TRUNCATE TABLE {tableList} RESTART IDENTITY";
                    Console.WriteLine(sql);
                    await using var cmd = new NpgsqlCommand(sql, conn, tx);
                    await cmd.ExecuteNonQueryAsync();

                    await tx.CommitAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error during truncation: " + ex.Message);
                    await tx.RollbackAsync();
                    return 2;
                }
            }

            Console.WriteLine("Truncation finished. Backups are in: " + backupDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed: " + ex.Message);
            return 1;
        }
    }

    private static string EscapeCsv(string s)
    {
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string BuildPostgresConnectionString(string raw)
    {
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
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

    private static string MaskPassword(string cs)
    {
        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("Password=", StringComparison.OrdinalIgnoreCase) || parts[i].StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
            {
                var idx = parts[i].IndexOf('=');
                if (idx >= 0) parts[i] = parts[i].Substring(0, idx + 1) + "****";
            }
        }
        return string.Join(";", parts) + ";";
    }
}
