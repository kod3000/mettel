namespace Bruin.Web.Wasm.Models;

// Typed error surfaced by BruinApiClient when the API returns
// ProblemDetails. Mirrors ApiError in apps/web/src/api/client.ts. The
// `Slug` is the last URI segment of `problem.type`, matching the React
// slugOf() helper — this is what UI code switches on for user-facing
// messaging.
public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string? Slug { get; }
    public ProblemDetails Problem { get; }

    public ApiException(ProblemDetails p)
        : base(p.Title ?? $"HTTP {p.Status ?? 0}")
    {
        Problem = p;
        StatusCode = p.Status ?? 0;
        Slug = SlugOf(p.Type);
    }

    public bool IsSlug(string s) => Slug == s;

    public static string? SlugOf(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var slash = type.LastIndexOf('/');
        return slash >= 0 && slash + 1 < type.Length ? type[(slash + 1)..] : type;
    }
}
