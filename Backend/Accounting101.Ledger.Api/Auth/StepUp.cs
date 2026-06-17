using Microsoft.AspNetCore.Authorization;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Requires that the caller authenticated within <see cref="Window"/> — "step-up" re-auth for the most
/// dangerous actions (e.g. reopen). Authentication happens upstream at the IdP; the host only gates on
/// recency, read from the OIDC <c>auth_time</c> claim.
/// </summary>
public sealed class StepUpRequirement(TimeSpan window) : IAuthorizationRequirement
{
    public TimeSpan Window { get; } = window;
}

/// <summary>Succeeds when the principal's <c>auth_time</c> claim is within the requirement's window.</summary>
public sealed class StepUpAuthorizationHandler : AuthorizationHandler<StepUpRequirement>
{
    /// <summary>Policy name for endpoints that require recent (stepped-up) authentication.</summary>
    public const string Policy = "StepUp";

    /// <summary>The OIDC claim carrying when the principal last authenticated (seconds since epoch).</summary>
    public const string AuthTimeClaim = "auth_time";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        if (context.User.FindFirst(AuthTimeClaim)?.Value is { } value
            && long.TryParse(value, out long unixSeconds)
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds) <= requirement.Window)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
