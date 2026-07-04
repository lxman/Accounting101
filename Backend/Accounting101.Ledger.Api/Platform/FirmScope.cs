using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Per-request holder for the resolved firm and its control database, populated by
/// <see cref="FirmResolutionMiddleware"/> and read by the scoped control-plane services. The Require*
/// accessors fail loudly if a service resolves it before the middleware ran (a wiring bug, not a runtime
/// input error).
/// </summary>
public sealed class FirmScope
{
    public FirmRegistration? Firm { get; set; }
    public IMongoDatabase? ControlDatabase { get; set; }

    public FirmRegistration RequireFirm() =>
        Firm ?? throw new InvalidOperationException(
            "Firm not resolved for this request; FirmResolutionMiddleware did not run.");

    public IMongoDatabase RequireControlDatabase() =>
        ControlDatabase ?? throw new InvalidOperationException(
            "Firm control database not resolved for this request; FirmResolutionMiddleware did not run.");
}
