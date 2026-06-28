namespace Accounting101.Payables;

/// <summary>
/// A ledger call returned a non-success status. Carries the engine's HTTP status code and its reason
/// (the ProblemDetails <c>detail</c>, or the raw body) so the module can surface the real cause — a
/// closed-period 409, an unbalanced-entry 422, a reverse-forbidden 403 — instead of letting it escape
/// as an opaque 500.
/// </summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    /// <summary>The HTTP status the engine returned (e.g. 403, 409, 422).</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The engine's human-readable reason, suitable to relay to the caller.</summary>
    public string Reason { get; } = reason;
}
