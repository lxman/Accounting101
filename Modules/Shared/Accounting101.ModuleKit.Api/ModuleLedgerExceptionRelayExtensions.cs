using Accounting101.ModuleKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.ModuleKit.Api;

/// <summary>
/// Registers the single relay that translates any escaping <see cref="LedgerClientException"/> into a
/// <c>application/problem+json</c> response carrying the engine's own status and reason — so a ledger
/// refusal never escapes a module endpoint as an opaque 500. Genuine engine 500s relay as 500 (honest);
/// only a clean 4xx that would otherwise be swallowed is straightened out.
/// </summary>
public static class ModuleLedgerExceptionRelayExtensions
{
    public static IApplicationBuilder UseModuleLedgerExceptionRelay(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (LedgerClientException ex)
            {
                if (ctx.Response.HasStarted) throw; // headers already sent — cannot reshape
                ctx.Response.StatusCode = ex.StatusCode;
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = ex.StatusCode,
                    Title = "Ledger request failed",
                    Detail = ex.Reason,
                }, options: null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
            }
        });
}
