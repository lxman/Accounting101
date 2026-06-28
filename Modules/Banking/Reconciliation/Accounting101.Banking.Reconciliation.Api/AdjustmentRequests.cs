using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

public sealed record RecordAdjustmentRequest(Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo);

public sealed record VoidReasonRequest(string? Reason);
