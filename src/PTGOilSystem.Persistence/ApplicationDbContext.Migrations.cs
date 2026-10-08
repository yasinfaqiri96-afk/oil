using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace PTGOilSystem.Web.Data;

public partial class ApplicationDbContext
{
    public const string MigrationsAssemblyName = "PTGOilSystem.Migrations";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Also covers contexts constructed directly by tools/tests. Only apply
        // the default to Npgsql; SQLite/InMemory and explicit overrides keep
        // their existing options. No connection or model configuration changes.
        var provider = optionsBuilder.Options.Extensions
            .OfType<RelationalOptionsExtension>()
            .SingleOrDefault();
        if (provider is not null
            && provider.GetType().Assembly == typeof(NpgsqlDbContextOptionsBuilder).Assembly
            && provider.MigrationsAssembly is null)
        {
            optionsBuilder.UseNpgsql(npgsql => npgsql.MigrationsAssembly(MigrationsAssemblyName));
        }

        base.OnConfiguring(optionsBuilder);
    }
}
