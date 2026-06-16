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

    /// <summary>If set, this is a control account requiring that dimension on every posting (e.g. A/R → Customer).</summary>
    public DimensionKind? RequiredDimension { get; init; }

    /// <summary>The single equity account that absorbs the temporary accounts at year-end close.</summary>
    public bool IsRetainedEarnings { get; init; }

    public bool Active { get; init; } = true;

    public Direction NormalSide => Type.NormalSide();
    public bool IsTemporary => Type.IsTemporary();
}
