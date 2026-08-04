using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bruin.Api.Features.Inventory;

// v1.<b64url(payload)>.<b64url(hmac-sha256(payload, key))>
//
// Payload carries:
//   v         — cursor version, currently 1
//   clientId  — the tenant the cursor was issued to; cross-checked against
//               the authenticated request's tenant on every page (400 if not)
//   sort/dir  — pinned so the caller can't rotate sort mid-scan
//   filterHash — sha-256 over the canonicalised filter tuple; different hash
//               means the filter set changed => 400 cursor-stale
//   key       — [sortValue-as-string, lastId] — the row-value keyset tuple
//
// Signature protects against tampering with any of the above.
public sealed class CursorCodec
{
    private readonly byte[] _key;
    public CursorCodec(byte[] key) { _key = key; }

    public string Encode(CursorPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var body = Base64Url(json);
        var sig = Base64Url(HmacSha256(json, _key));
        return $"v1.{body}.{sig}";
    }

    public bool TryDecode(string cursor, out CursorPayload payload, out string errorSlug)
    {
        payload = default!;
        errorSlug = "cursor-invalid";
        if (string.IsNullOrEmpty(cursor)) return false;
        var parts = cursor.Split('.');
        if (parts.Length != 3 || parts[0] != "v1") return false;

        byte[] body;
        try { body = Base64UrlDecode(parts[1]); }
        catch { return false; }

        byte[] sig;
        try { sig = Base64UrlDecode(parts[2]); }
        catch { return false; }

        var expect = HmacSha256(body, _key);
        // CryptographicOperations.FixedTimeEquals to keep timing side-channel
        // out of the failure path — a public HMAC is still worth the discipline.
        if (!CryptographicOperations.FixedTimeEquals(sig, expect))
            return false;

        try
        {
            var deserialized = JsonSerializer.Deserialize<CursorPayload>(body, JsonOpts);
            if (deserialized is null || deserialized.V != 1) return false;
            payload = deserialized;
            return true;
        }
        catch { return false; }
    }

    // Canonical, deterministic serialization of a filter set so cursor bakes
    // in what the client asked for; changing any of these must invalidate.
    public static string FilterHash(ListQuery q)
    {
        // Order-stable: sort array values so ?status=a&status=b hashes the same
        // as ?status=b&status=a — the API contract treats them as OR sets.
        var obj = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["q"] = q.Q,
            ["statuses"]   = q.Statuses.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            ["categories"] = q.Categories.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            ["states"]     = q.States.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] body, byte[] key)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(body);
    }

    private static string Base64Url(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var pad = s.Length % 4;
        if (pad > 0) s = s.PadRight(s.Length + (4 - pad), '=');
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/'));
    }

    // System.Text.Json opts scoped to the cursor path so nothing else picks up
    // the more permissive naming policy.
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record CursorPayload(
    int V,
    Guid ClientId,
    string Sort,
    string Dir,
    string FilterHash,
    // key[0] = sortValue serialized as string (null for NULLS-LAST rows)
    // key[1] = lastId (guid string)
    string?[] Key);
