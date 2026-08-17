using System.Net;
using System.Text.Json;
using Xunit;

namespace Bruin.Api.Tests;

// /debug/lsn shape smoke — E1. Doesn't assert on lag values (they're
// environment-dependent), just proves the endpoint answers with the
// expected fields so the SPA's LSN bar keeps rendering.
[Collection(PostgresCollection.Name)]
public sealed class DebugLsnTests
{
    private readonly PostgresFixture _fx;
    public DebugLsnTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Debug_lsn_returns_shape()
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", PostgresFixture.ApiKeyA_Admin);
        using var res = await c.GetAsync("/api/v1/debug/lsn");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Testcontainers "replica" == primary, so both LSNs should be non-null strings.
        Assert.False(string.IsNullOrEmpty(root.GetProperty("primary").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("replica").GetString()));
        Assert.True(root.GetProperty("reachable").GetBoolean());
        Assert.True(root.GetProperty("lagBytes").GetInt64() >= 0);
    }
}
