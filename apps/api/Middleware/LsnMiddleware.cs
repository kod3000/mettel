using Bruin.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Bruin.Api.Middleware;

// Bridges the LSN header protocol between the HTTP layer and the read
// router. Runs after the X-Api-Key middleware so tenant is bound before
// we care about LSN routing.
//
// - Read side: extracts `X-Min-LSN` if present and pushes it into the
//   scoped `ILsnContext`.
// - Write side: registers an OnStarting hook that reads the LsnContext's
//   `WriteLsn` (set by the write handler after commit) and stamps
//   `X-Write-LSN` on the response. If the handler didn't set a value, we
//   emit nothing — clients only track LSNs from actual writes.
public sealed class LsnMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ILsnContext lsn)
    {
        if (ctx.Request.Headers.TryGetValue("X-Min-LSN", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try { lsn.SetMinLsn(raw.ToString()); }
            catch (ArgumentException)
            {
                // Fall through — bad LSN just means we can't route smartly;
                // the read path still works, just without the guarantee.
            }
        }

        ctx.Response.OnStarting(() =>
        {
            if (lsn.WriteLsn is not null)
                ctx.Response.Headers.Append("X-Write-LSN", lsn.WriteLsn);
            return Task.CompletedTask;
        });

        await next(ctx);
    }
}
