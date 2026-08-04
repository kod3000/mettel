using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Bruin.Api.Errors;

// Helpers so every ProblemDetails response carries the stable `type` slug and
// the `errors` map when relevant. Handlers stay short — they call one of these
// rather than assembling the envelope inline.
public static class Problem
{
    public static IResult ValidationFailed(IDictionary<string, string[]> fieldErrors, string? detail = null)
        => Results.Problem(new ProblemDetails
        {
            Type = ErrorSlugs.TypeUri(ErrorSlugs.ValidationFailed),
            Title = "Validation failed",
            Status = StatusCodes.Status400BadRequest,
            Detail = detail ?? "One or more fields are invalid.",
            Extensions = { ["errors"] = fieldErrors },
        });

    public static IResult BadRequest(string slug, string title, string? detail = null)
        => Results.Problem(new ProblemDetails
        {
            Type = ErrorSlugs.TypeUri(slug),
            Title = title,
            Status = StatusCodes.Status400BadRequest,
            Detail = detail,
        });

    public static IResult NotFound(string? detail = null)
        => Results.Problem(new ProblemDetails
        {
            Type = ErrorSlugs.TypeUri(ErrorSlugs.NotFound),
            Title = "Not found",
            Status = StatusCodes.Status404NotFound,
            Detail = detail,
        });

    public static IResult Unauthorized(string? detail = null)
        => Results.Problem(new ProblemDetails
        {
            Type = ErrorSlugs.TypeUri(ErrorSlugs.Unauthorized),
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = detail,
        });

    public static IResult Conflict(string slug, string title, string? detail = null)
        => Results.Problem(new ProblemDetails
        {
            Type = ErrorSlugs.TypeUri(slug),
            Title = title,
            Status = StatusCodes.Status409Conflict,
            Detail = detail,
        });
}
