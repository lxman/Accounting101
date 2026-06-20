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

    /// <summary>
    /// If set, this is a control account requiring that dimension <em>type</em> on every posting line
    /// (e.g. A/R → "Customer"). A free-string key the engine never interprets — it only enforces the
    /// line carries a tag of that type.
    /// </summary>
    public string? RequiredDimension { get; init; }

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
