namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// A single posting within a journal entry: a signed amount against one account,
/// tagged Debit or Credit. Sparse subledger dimensions are present only on lines
/// that touch a control account.
/// </summary>
public sealed record Line
{
    /// <summary>Stable line identity — reconciliation and attachments reference this.</summary>
    public required Guid Id { get; init; }

    /// <summary>The (leaf) account posted to, by its stable internal id — never the account number.</summary>
    public required Guid AccountId { get; init; }

    public required Direction Direction { get; init; }

    /// <summary>
    /// Signed amount; either sign is allowed (a negative debit equals a positive
    /// credit). Money is <see cref="decimal"/>, never floating point.
    /// </summary>
    public required decimal Amount { get; init; }

    // Sparse subledger dimensions — set only where the account is a control account.
    public Guid? CustomerId { get; init; }
    public Guid? VendorId { get; init; }
    public Guid? ItemId { get; init; }

    public string? LineMemo { get; init; }

    /// <summary>
    /// Debit-positive signed effect used by replay: Debit =&gt; +Amount, Credit =&gt; -Amount.
    /// An entry is valid exactly when its lines' signed effects sum to zero.
    /// </summary>
    public decimal SignedEffect => Direction == Direction.Debit ? Amount : -Amount;
}
