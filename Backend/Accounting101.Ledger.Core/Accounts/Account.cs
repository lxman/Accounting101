using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Accounts;

/// <summary>
/// A chart-of-accounts entry — reference data, not balances. The journal references accounts by
/// the stable <see cref="Id"/>; <see cref="Number"/> is the accountant-facing, renumberable code.
/// Normal balance and temporary/permanent are derived from <see cref="Type"/>, never stored.
/// </summary>
public sealed record Account
{
    public required Guid Id { get; init; }
    public required Guid ClientId { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public required AccountType Type { get; init; }

    /// <summary>Parent in the rollup hierarchy; null for a root. A child shares its parent's type.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Whether entries may post here. Summary/header accounts are not postable.</summary>
    public bool Postable { get; init; } = true;

    /// <summary>Dimension types every posting line touching this account MUST carry. Empty = unconstrained.
    /// A control account (e.g. A/R) may require several (Customer AND Invoice).</summary>
    public IReadOnlyCollection<string> RequiredDimensions { get; init; } = [];

    /// <summary>Legacy single-dimension accessor: the first required dimension, or null. Retained for callers/
    /// responses that predate the set. Prefer <see cref="RequiredDimensions"/>.</summary>
    public string? RequiredDimension => RequiredDimensions.Count == 0 ? null : RequiredDimensions.First();

    /// <summary>
    /// Which statement-of-cash-flows activity this account's movements represent. Null falls back to a
    /// type-based default (<see cref="AccountTypeExtensions.DefaultCashFlowActivity"/>); the cash accounts
    /// and the investing/financing exceptions (fixed assets, loans, stock) are tagged explicitly — the same
    /// kind of declared classification as <see cref="Type"/>, never a stored value.
    /// </summary>
    public CashFlowActivity? CashFlowActivity { get; init; }

    /// <summary>The single equity account that absorbs the temporary accounts at year-end close.</summary>
    public bool IsRetainedEarnings { get; init; }

    public bool Active { get; init; } = true;

    public Direction NormalSide => Type.NormalSide();
    public bool IsTemporary => Type.IsTemporary();
}
