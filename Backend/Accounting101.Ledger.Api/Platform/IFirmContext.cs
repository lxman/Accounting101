namespace Accounting101.Ledger.Api.Platform;

/// <summary>Resolves the firm the current request acts within, from the authenticated principal's
/// <see cref="FirmClaims.FirmId"/> claim, or the configured default when the claim is absent.</summary>
public interface IFirmContext
{
    Guid FirmId { get; }
}
