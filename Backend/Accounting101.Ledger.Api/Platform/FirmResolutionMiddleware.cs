using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Resolves the current request's firm (from <see cref="IFirmContext"/>) into the scoped
/// <see cref="FirmScope"/>: looks the firm up in the platform registry, gets the client for the firm's
/// cluster, and records the firm + its control database. An unknown or suspended firm is refused with 403
/// before any endpoint runs. Runs after authentication so the firm claim is available.
/// </summary>
public sealed class FirmResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, IFirmContext firmContext, PlatformStore platform,
        IMongoClientFactory factory, FirmScope scope)
    {
        // The platform-operator control plane (/platform/*) operates on the platform registry, not on any
        // firm's data, and never reads FirmScope — so it must not depend on firm resolution. Otherwise
        // suspending the default firm would lock operators out of the very endpoint that reactivates it.
        if (context.Request.Path.StartsWithSegments("/platform"))
        {
            await next(context);
            return;
        }

        Guid firmId = firmContext.FirmId;
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, context.RequestAborted);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Unknown or suspended firm.",
            }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            return;
        }

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, context.RequestAborted);
        scope.Firm = firm;
        scope.ControlDatabase = client.GetDatabase(firm.ControlDatabase);
        await next(context);
    }
}

/// <summary>Pipeline registration for <see cref="FirmResolutionMiddleware"/>.</summary>
public static class FirmResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseFirmResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<FirmResolutionMiddleware>();
}
