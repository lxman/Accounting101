using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

public sealed record RecordBankStatementRequest(
    Guid CashAccountId, DateOnly StatementDate, decimal OpeningBalance, decimal ClosingBalance,
    IReadOnlyList<BankStatementLineRequest> Lines);

public sealed record BankStatementLineRequest(DateOnly Date, decimal Amount, string Description, string? ExternalRef);

public sealed record StartReconciliationRequest(Guid BankStatementId);

public sealed record ClearRequest(IReadOnlyList<Guid> EntryIds);
