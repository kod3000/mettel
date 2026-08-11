using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bruin.Api.Data;

// Consumed only by `dotnet ef` at design time. Runtime wires the DbContext up
// in Program.cs against the primary connection string.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BruinDbContext>
{
    public BruinDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("BRUIN_DB_PRIMARY")
            ?? "Host=localhost;Port=5432;Database=bruin;Username=bruin;Password=bruin";
        var options = new DbContextOptionsBuilder<BruinDbContext>()
            .UseNpgsql(conn, o => o.MigrationsHistoryTable("__ef_migrations_history"))
            .Options;
        return new BruinDbContext(options);
    }
}
