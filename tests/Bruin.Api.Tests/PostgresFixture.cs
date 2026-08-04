using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Bruin.Api.Tests;

// One Postgres container per test collection — starting from a fresh 17-alpine
// each time keeps blast radius small when a test leaves rows behind. The API
// is booted in-process via WebApplicationFactory<Program> and pointed at the
// container's connection string; migrations run on start-up as in production.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    public string ConnString { get; private set; } = "";
    public BruinAppFactory Factory { get; private set; } = null!;

    public Guid ClientA { get; } = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public Guid ClientB { get; } = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public const string ApiKeyA = "test-key-a";
    public const string ApiKeyB = "test-key-b";

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("bruin")
            .WithUsername("bruin")
            .WithPassword("bruin")
            .Build();
        await _pg.StartAsync();
        ConnString = _pg.GetConnectionString();

        Factory = new BruinAppFactory(ConnString);
        // Force the app to boot so migrations run.
        _ = Factory.Server;

        await SeedClientsAsync();
    }

    private async Task SeedClientsAsync()
    {
        await using var c = new NpgsqlConnection(ConnString);
        await c.OpenAsync();
        foreach (var (id, key, name) in new[]
        {
            (ClientA, ApiKeyA, "Test Client A"),
            (ClientB, ApiKeyB, "Test Client B"),
        })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO public.client (id, name, api_key) VALUES (@i, @n, @k)
                                ON CONFLICT (id) DO NOTHING";
            cmd.Parameters.AddWithValue("i", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("k", key);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Bulk insert n rows for a given tenant. Uses binary COPY so we can seed
    // thousands per test without paying INSERT overhead. Rows are generated
    // deterministically so a failing test can be replayed.
    public async Task SeedInventoryAsync(Guid clientId, int rows, int seed = 42, DateTimeOffset? baseTime = null)
    {
        var rng = new Random(seed);
        var t0 = baseTime ?? DateTimeOffset.UtcNow.AddDays(-30);
        await using var c = new NpgsqlConnection(ConnString);
        await c.OpenAsync();
        // Drop the tsv column + secondary indexes so this is fast in tests too.
        // We recreate them afterwards, same as the production seeder.
        await Exec(c, "ALTER TABLE public.inventory DROP COLUMN IF EXISTS search_tsv");
        foreach (var idx in new[]{
            "ux_inventory_client_service","ix_inventory_client_created_id",
            "ix_inventory_client_updated_id","ix_inventory_client_status_created",
            "ix_inventory_client_tsv","ix_inventory_client_service_trgm"})
            await Exec(c, $"DROP INDEX IF EXISTS public.{idx}");

        using (var writer = c.BeginBinaryImport(@"
            COPY public.inventory
            (id, client_id, service_number, product_category, product_name, status,
             city, state, address, assignee, notes, created_at, updated_at, row_version)
            FROM STDIN (FORMAT BINARY)"))
        {
            for (int i = 0; i < rows; i++)
            {
                var createdAt = t0.AddSeconds(i);   // strictly monotonic → keyset determinism
                var updatedAt = createdAt;
                var status = (i % 20) switch { < 3 => "pending", < 17 => "active", _ => "disconnected" };
                var cat    = (i % 4) switch { 0 => "voice", 1 => "data", 2 => "wireless", _ => "other" };
                writer.StartRow();
                writer.Write(Guid.CreateVersion7(createdAt), NpgsqlDbType.Uuid);
                writer.Write(clientId, NpgsqlDbType.Uuid);
                writer.Write($"555-{clientId.ToString()[..3]}-{i:D6}", NpgsqlDbType.Varchar);
                writer.Write(cat, NpgsqlDbType.Varchar);
                writer.Write($"Product #{i}", NpgsqlDbType.Varchar);
                writer.Write(status, NpgsqlDbType.Varchar);
                writer.Write("Test City", NpgsqlDbType.Varchar);
                writer.Write("NY", NpgsqlDbType.Varchar);
                writer.Write($"{100 + i} Main St", NpgsqlDbType.Varchar);
                writer.WriteNull();
                writer.WriteNull();
                writer.Write(createdAt, NpgsqlDbType.TimestampTz);
                writer.Write(updatedAt, NpgsqlDbType.TimestampTz);
                writer.Write(1, NpgsqlDbType.Integer);
            }
            writer.Complete();
        }

        await Exec(c, @"
            ALTER TABLE public.inventory
            ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
                setweight(to_tsvector('simple', coalesce(product_name, '')), 'A') ||
                setweight(to_tsvector('simple', coalesce(address, '')),      'B') ||
                setweight(to_tsvector('simple', coalesce(notes, '')),        'C')
            ) STORED");
        await Exec(c, "CREATE UNIQUE INDEX ux_inventory_client_service ON public.inventory (client_id, service_number)");
        await Exec(c, "CREATE INDEX ix_inventory_client_created_id ON public.inventory (client_id, created_at DESC, id DESC)");
        await Exec(c, "CREATE INDEX ix_inventory_client_updated_id ON public.inventory (client_id, updated_at DESC, id DESC)");
        await Exec(c, "CREATE INDEX ix_inventory_client_status_created ON public.inventory (client_id, status, created_at DESC, id DESC)");
        await Exec(c, "CREATE INDEX ix_inventory_client_tsv ON public.inventory USING gin (client_id, search_tsv)");
        await Exec(c, "CREATE INDEX ix_inventory_client_service_trgm ON public.inventory USING gin (client_id, service_number gin_trgm_ops)");
        await Exec(c, "ANALYZE public.inventory");
    }

    public async Task TruncateInventoryAsync()
    {
        await using var c = new NpgsqlConnection(ConnString);
        await c.OpenAsync();
        await Exec(c, "TRUNCATE public.inventory");
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        if (_pg is not null) await _pg.DisposeAsync();
    }
}

// WebApplicationFactory that overrides the app's connection strings so it
// talks to the Testcontainers Postgres rather than the compose one.
public sealed class BruinAppFactory : WebApplicationFactory<Program>
{
    private readonly string _conn;
    public BruinAppFactory(string conn) { _conn = conn; }

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Primary", _conn);
        b.UseSetting("ConnectionStrings:Replica", _conn);
        b.UseSetting("Cursor:HmacKey", "test-cursor-key");
        // Bulk-job uploads land under the OS temp dir when running under
        // tests — /uploads (the compose default) doesn't exist in a bare
        // dotnet test process.
        Environment.SetEnvironmentVariable("BRUIN_UPLOAD_DIR",
            Path.Combine(Path.GetTempPath(), "bruin-tests-uploads"));
        b.UseEnvironment("Testing");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
