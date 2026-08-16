namespace Bruin.Api.Errors;

// Stable slugs the frontend switches on. Wire type = "<base>/<slug>". Keep
// this list in lock-step with docs/API_CONTRACT.md — if you rename a slug
// here without amending the contract, the generated TS client's typed error
// union drifts.
public static class ErrorSlugs
{
    public const string TypeBase = "https://bruin.example/errors";

    public const string ValidationFailed         = "validation-failed";
    public const string CursorInvalid            = "cursor-invalid";
    public const string CursorStale              = "cursor-stale";
    public const string InvalidStatusTransition  = "invalid-status-transition";
    public const string DuplicateServiceNumber   = "duplicate-service-number";
    public const string ConcurrencyConflict      = "concurrency-conflict";
    public const string NotFound                 = "not-found";
    public const string UnsupportedMediaType     = "unsupported-media-type";
    public const string PayloadTooLarge          = "payload-too-large";
    public const string Unauthorized             = "unauthorized";
    public const string Forbidden                = "forbidden";

    public static string TypeUri(string slug) => $"{TypeBase}/{slug}";
}
