using Bruin.Web.Wasm.Models;

namespace Bruin.Web.Wasm.Services;

// Mirror of apps/web/src/api/reportError.ts. Split behavior for mutation
// failures:
//   - ProblemDetails with a populated Errors map → return it so the caller
//     can render per-field messages inline. NO toast (field-level UI is
//     the primary channel).
//   - Everything else (5xx, network, ProblemDetails without Errors,
//     non-ApiException throws) → toast the best message.
public sealed class ErrorReporter
{
    private readonly ToastService _toasts;

    public ErrorReporter(ToastService toasts) => _toasts = toasts;

    // Returns the field-errors map when the caller should render them
    // inline; null when we've already toasted (or nothing to render).
    public IReadOnlyDictionary<string, string[]>? Report(Exception ex, string? context = null)
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context} — ";

        if (ex is ApiException api)
        {
            if (api.Problem.Errors is { Count: > 0 } errs)
            {
                return errs;
            }
            var msg = api.Problem.Detail ?? api.Problem.Title ?? $"HTTP {api.StatusCode}";
            _toasts.Error($"{prefix}{msg}");
            return null;
        }

        _toasts.Error($"{prefix}{ex.Message}");
        return null;
    }
}
