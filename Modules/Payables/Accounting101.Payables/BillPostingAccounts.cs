namespace Accounting101.Payables;

/// <summary>The single chart account the bill recipe credits. Expense accounts come from the bill lines,
/// so they are not configured here.</summary>
public sealed record BillPostingAccounts
{
    /// <summary>Accounts Payable — the control account credited for the bill total, tagged by vendor.</summary>
    public required Guid PayableAccountId { get; init; }
}
