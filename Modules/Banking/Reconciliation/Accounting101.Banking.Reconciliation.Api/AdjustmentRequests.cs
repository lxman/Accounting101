using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>Record a bank adjustment against a reconciliation. <paramref name="Id"/> is an optional
/// caller-supplied retry key for the adjustment itself — unrelated to the reconciliation id in the
/// route. Same key and same content replays, different content is a 422.</summary>
public sealed record RecordAdjustmentRequest(
    Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo, Guid? Id = null);

public sealed record VoidReasonRequest(string? Reason);
