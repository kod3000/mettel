namespace Bruin.Api.Domain;

// Per-request state for the read-your-own-writes protocol.
//
// - MinLsn: the value the client sent as `X-Min-LSN` (populated by the
//   LsnMiddleware early in the pipeline). Consumed by the read router when
//   deciding replica vs primary.
// - WriteLsn: set by mutation handlers via `RecordWrite(lsn)` after a
//   successful commit. The response filter then serialises it as
//   `X-Write-LSN` on the response.
//
// PG LSNs are `<hi>/<lo>` hex strings — kept as strings on the wire so we
// don't accidentally coerce to a number and lose precision.
public interface ILsnContext
{
    string? MinLsn { get; }
    string? WriteLsn { get; }
    void SetMinLsn(string lsn);
    void RecordWrite(string lsn);
}

public sealed class LsnContext : ILsnContext
{
    public string? MinLsn { get; private set; }
    public string? WriteLsn { get; private set; }

    public void SetMinLsn(string lsn)
    {
        if (string.IsNullOrWhiteSpace(lsn))
            throw new ArgumentException("LSN must not be empty", nameof(lsn));
        MinLsn = lsn;
    }

    public void RecordWrite(string lsn)
    {
        if (string.IsNullOrWhiteSpace(lsn))
            throw new ArgumentException("LSN must not be empty", nameof(lsn));
        WriteLsn = lsn;
    }
}

// LSN comparison helper. PG LSN is `<hi>/<lo>` in hex; treat as (uint64 hi
// << 32) | uint32 lo for ordering. Kept here rather than in DbConnections so
// unit tests can exercise it without a DB.
public static class LsnCompare
{
    public static ulong Parse(string lsn)
    {
        var slash = lsn.IndexOf('/');
        if (slash <= 0 || slash == lsn.Length - 1)
            throw new FormatException($"invalid LSN '{lsn}' — expected '<hi>/<lo>'");
        var hi = uint.Parse(lsn.AsSpan(0, slash), System.Globalization.NumberStyles.HexNumber);
        var lo = uint.Parse(lsn.AsSpan(slash + 1), System.Globalization.NumberStyles.HexNumber);
        return ((ulong)hi << 32) | lo;
    }

    // true when a >= b, i.e. replica at `a` has caught up past `b`.
    public static bool Caught(string a, string b) => Parse(a) >= Parse(b);
}
