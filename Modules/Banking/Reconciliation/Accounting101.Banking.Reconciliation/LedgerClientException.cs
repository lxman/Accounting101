namespace Accounting101.Banking.Reconciliation;

/// <summary>A ledger call returned a non-success status. Carries the engine's HTTP status and reason so the
/// module can relay the real cause (a closed-period 409, an unknown-account 422) instead of an opaque 500.</summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    public int StatusCode { get; } = statusCode;
    public string Reason { get; } = reason;
}
