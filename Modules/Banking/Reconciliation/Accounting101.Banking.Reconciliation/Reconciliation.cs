using System.Text.Json.Serialization;

namespace Accounting101.Banking.Reconciliation;

/// <summary>A reconciliation of one bank statement: the working record of which ledger cash entries have
/// been cleared (matched as appearing on the bank). Editable while InProgress; flips to Completed when balanced.</summary>
public sealed record Reconciliation
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // "REC-{n:D5}"
    public required Guid CashAccountId { get; init; }
    public required Guid BankStatementId { get; init; }
    public required DateOnly StatementDate { get; init; }
    public ReconciliationStatus Status { get; init; } = ReconciliationStatus.InProgress;
    public required IReadOnlyList<Guid> ClearedEntryIds { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReconciliationStatus { InProgress, Completed }
