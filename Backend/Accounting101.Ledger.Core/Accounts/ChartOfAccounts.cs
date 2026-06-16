namespace Accounting101.Ledger.Core.Accounts;

/// <summary>
/// A client's chart of accounts as a validated aggregate: lookups by id/number, hierarchy
/// navigation, and the structural invariants (parents exist, no cycles, a child shares its
/// parent's type, account numbers are unique, at most one retained-earnings account).
/// Construction throws <see cref="InvalidChartOfAccountsException"/> if any invariant is violated,
/// so an invalid chart cannot be represented.
/// </summary>
public sealed class ChartOfAccounts
{
    private readonly Dictionary<Guid, Account> _byId;
    private readonly ILookup<Guid?, Account> _byParent;

    public ChartOfAccounts(IEnumerable<Account> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        IReadOnlyList<Account> all = accounts.ToList();

        _byId = BuildById(all);
        _byParent = all.ToLookup(a => a.ParentId);
        Validate(all, _byId);
    }

    public IReadOnlyCollection<Account> Accounts => _byId.Values;

    public Account? Find(Guid id) => _byId.GetValueOrDefault(id);

    public Account? FindByNumber(string number) => _byId.Values.FirstOrDefault(a => a.Number == number);

    /// <summary>Top-level accounts (no parent).</summary>
    public IEnumerable<Account> Roots => _byParent[null];

    public IEnumerable<Account> Children(Guid id) => _byParent[id];

    /// <summary>True if the account has no children (postings land on leaves).</summary>
    public bool IsLeaf(Guid id) => !_byParent[id].Any();

    /// <summary>The designated retained-earnings account, if one is flagged.</summary>
    public Account? RetainedEarnings => _byId.Values.FirstOrDefault(a => a.IsRetainedEarnings);

    /// <summary>All accounts beneath the given one, depth-first.</summary>
    public IEnumerable<Account> Descendants(Guid id)
    {
        foreach (Account child in _byParent[id])
        {
            yield return child;
            foreach (Account descendant in Descendants(child.Id))
                yield return descendant;
        }
    }

    private static Dictionary<Guid, Account> BuildById(IReadOnlyList<Account> all)
    {
        Dictionary<Guid, Account> byId = new(all.Count);
        foreach (Account account in all)
        {
            if (!byId.TryAdd(account.Id, account))
                throw new InvalidChartOfAccountsException($"Duplicate account id {account.Id}.");
        }

        return byId;
    }

    private static void Validate(IReadOnlyList<Account> all, Dictionary<Guid, Account> byId)
    {
        HashSet<string> numbers = new(StringComparer.Ordinal);
        foreach (Account account in all)
        {
            if (!numbers.Add(account.Number))
                throw new InvalidChartOfAccountsException($"Duplicate account number '{account.Number}'.");
        }

        if (all.Count(a => a.IsRetainedEarnings) > 1)
            throw new InvalidChartOfAccountsException("More than one retained-earnings account.");

        // Parent must exist and a child must share its parent's type.
        foreach (Account account in all)
        {
            if (account.ParentId is not { } parentId)
                continue;

            if (!byId.TryGetValue(parentId, out Account? parent))
                throw new InvalidChartOfAccountsException($"Account '{account.Number}' references a missing parent {parentId}.");

            if (parent.Type != account.Type)
                throw new InvalidChartOfAccountsException(
                    $"Account '{account.Number}' ({account.Type}) must share its parent '{parent.Number}' type ({parent.Type}).");
        }

        // Walking parents from any node must reach a root without revisiting one.
        foreach (Account account in all)
        {
            HashSet<Guid> seen = [];
            Account? current = account;
            while (current is { ParentId: { } parentId })
            {
                if (!seen.Add(current.Id))
                    throw new InvalidChartOfAccountsException($"Cycle in the account hierarchy at '{account.Number}'.");

                current = byId[parentId];
            }
        }
    }
}
