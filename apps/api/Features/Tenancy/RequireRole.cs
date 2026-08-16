using Bruin.Api.Domain;
using Bruin.Api.Errors;

namespace Bruin.Api.Features.Tenancy;

// Tiny endpoint filter for role gating. Usage:
//   r.MapPost("/api/v1/inventory", CreateAsync).RequireRole(Roles.Worker, Roles.Admin);
//
// Role hierarchy is flat — worker does NOT imply admin, admin does NOT
// imply worker. Callers list every role that's allowed for the endpoint
// so the intent is explicit at the route (grep-friendly for auditors).
public static class RequireRoleExtensions
{
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, params string[] allowed)
        where TBuilder : IEndpointConventionBuilder
    {
        var allowSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var tenant = ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.Role is null || !allowSet.Contains(tenant.Role))
                return Problem.Forbidden(
                    "Insufficient role",
                    $"This endpoint requires one of: {string.Join(", ", allowed)}. Your key is '{tenant.Role ?? "unauthenticated"}'.");
            return await next(ctx);
        });
        return builder;
    }
}
