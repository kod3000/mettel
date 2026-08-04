using System.Net;
using System.Text.Json;
using Xunit;

namespace Bruin.Api.Tests;

// Phase 5 gate: verify the read router honours X-Min-LSN.
//
//   (i)  No X-Min-LSN                  → replica, no fallback counter tick.
//   (ii) X-Min-LSN far in the future   → primary,  fallback counter ticks by ≥ 1.
//   (iii)X-Min-LSN older than replica  → replica, no fallback counter tick.
//
// We infer routing via /metrics — the `bruin_read_primary_fallback_total`
// counter increments once per fallback. Cheaper and more honest than
// leaking routing detail into a response header.
[Collection(PostgresCollection.Name)]
public sealed class ReadRouterTests
{
    private readonly PostgresFixture _fx;
    public ReadRouterTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey, string? minLsn = null)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        if (minLsn is not null) c.DefaultRequestHeaders.Add("X-Min-LSN", minLsn);
        return c;
    }

    private static async Task<long> ReadFallbackCounter(HttpClient c)
    {
        // /metrics is served on the same host without X-Api-Key. Skip the
        // header pool by using a bare factory-issued client.
        using var res = await c.GetAsync("/metrics");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("bruin_read_primary_fallback_total ", StringComparison.Ordinal))
                return long.Parse(line["bruin_read_primary_fallback_total ".Length..].Trim());
        throw new InvalidOperationException("counter not found in /metrics output:\n" + body);
    }

    [Fact]
    public async Task No_min_lsn_uses_replica_and_does_not_fall_back()
    {
        await _fx.TruncateInventoryAsync();
        await _fx.SeedInventoryAsync(_fx.ClientA, 50);
        var client = Client(PostgresFixture.ApiKeyA);

        var before = await ReadFallbackCounter(client);
        using var res = await client.GetAsync("/api/v1/inventory?pageSize=10");
        res.EnsureSuccessStatusCode();
        var after = await ReadFallbackCounter(client);

        Assert.Equal(before, after);
    }

    // NOTE: full routing behaviour (primary vs replica selection under an
    // X-Min-LSN watermark) requires an actual streaming standby, which
    // Testcontainers doesn't give us. The unit tests below exercise the
    // routing logic directly with a fake IDbConnections + fake ReplicaState.
    // The `No_min_lsn_...` test above is the integration-side sanity: no
    // watermark => no fallback, on a live single-instance setup.

    [Fact]
    public async Task Router_falls_back_to_primary_when_replica_behind_min_lsn()
    {
        var db = new FakeConn();
        var lsn = new Bruin.Api.Domain.LsnContext();
        lsn.SetMinLsn("FF/FFFFFFFF");
        var replica = new FakeReplicaState { ReplayLsn = "0/100", Reachable = true };
        var metrics = new Bruin.Api.Observability.Metrics();

        var router = new Bruin.Api.Data.ReadRouter(db, lsn, replica, metrics);
        try { await router.OpenReadAsync(); } catch { /* fake conns throw */ }

        Assert.Equal(1, metrics.ReadPrimaryFallbackTotal);
        Assert.Equal("primary", db.LastRequested);
    }

    [Fact]
    public async Task Router_uses_replica_when_it_has_caught_up_to_min_lsn()
    {
        var db = new FakeConn();
        var lsn = new Bruin.Api.Domain.LsnContext();
        lsn.SetMinLsn("1/00000010");
        var replica = new FakeReplicaState { ReplayLsn = "1/00000020", Reachable = true };
        var metrics = new Bruin.Api.Observability.Metrics();

        var router = new Bruin.Api.Data.ReadRouter(db, lsn, replica, metrics);
        try { await router.OpenReadAsync(); } catch { }

        Assert.Equal(0, metrics.ReadPrimaryFallbackTotal);
        Assert.Equal("replica", db.LastRequested);
    }

    [Fact]
    public async Task Router_falls_back_to_primary_when_replica_unreachable()
    {
        var db = new FakeConn();
        var lsn = new Bruin.Api.Domain.LsnContext();
        // No min-LSN => normally replica, but replica is down.
        var replica = new FakeReplicaState { Reachable = false, ReplayLsn = null };
        var metrics = new Bruin.Api.Observability.Metrics();

        var router = new Bruin.Api.Data.ReadRouter(db, lsn, replica, metrics);
        try { await router.OpenReadAsync(); } catch { }

        Assert.Equal(1, metrics.ReadPrimaryFallbackTotal);
        Assert.Equal("primary", db.LastRequested);
    }

    // ---- doubles ------------------------------------------------------

    private sealed class FakeConn : Bruin.Api.Data.IDbConnections
    {
        public string? LastRequested;
        public ValueTask<Npgsql.NpgsqlConnection> OpenPrimaryAsync(CancellationToken ct = default)
        { LastRequested = "primary"; throw new NotSupportedException("fake"); }
        public ValueTask<Npgsql.NpgsqlConnection> OpenReplicaAsync(CancellationToken ct = default)
        { LastRequested = "replica"; throw new NotSupportedException("fake"); }
    }

    // Behavioural double for ReplicaState. Deliberately inherits so the
    // sealed router accepts it via the injected concrete-type parameter.
    // (Router depends on ReplicaState-the-class; keeping it that way spares
    // an extra interface for one production implementation.)
    private sealed class FakeReplicaState : Bruin.Api.Data.ReplicaState
    {
        public string? ReplayLsn { get; set; }
        public bool Reachable { get; set; } = true;
        public FakeReplicaState() : base(null!, new Bruin.Api.Observability.Metrics()) { }
        public override bool IsReachable => Reachable;
        public override Task<string?> GetReplayLsnAsync(CancellationToken ct = default) => Task.FromResult(ReplayLsn);
    }

    [Fact]
    public void LsnCompare_parses_and_orders_two_lsns()
    {
        // Regression guard for the string→ulong parsing that the router
        // depends on. Cheap standalone test — no fixture needed.
        Assert.True(Bruin.Api.Domain.LsnCompare.Caught("1/2C0D69A8", "1/2C0D68A8"));
        Assert.False(Bruin.Api.Domain.LsnCompare.Caught("1/2C0D68A8", "1/2C0D69A8"));
        Assert.True(Bruin.Api.Domain.LsnCompare.Caught("2/00000000", "1/FFFFFFFF"));
        Assert.False(Bruin.Api.Domain.LsnCompare.Caught("1/00000000", "2/00000000"));
    }
}
