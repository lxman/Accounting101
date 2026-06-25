namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Compares two entries on financial substance — the fields that define "the same operation" for
/// idempotent retry. Ignores engine-assigned (sequence/posted-at/audit), lifecycle (status/posting),
/// and descriptive (reference/memo) fields. Used by idempotent post to decide whether a re-post of an
/// existing id is a true replay (return the existing entry) or an id reused for different content.
/// </summary>
public static class EntryComparison
{
    public static bool SameFinancialContent(JournalEntry a, JournalEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.EffectiveDate != b.EffectiveDate) return false;
        if (a.Type != b.Type) return false;
        if (a.SourceRef != b.SourceRef) return false;
        if (!string.Equals(a.SourceType, b.SourceType, StringComparison.Ordinal)) return false;
        if (a.Lines.Count != b.Lines.Count) return false;

        for (int i = 0; i < a.Lines.Count; i++)
        {
            Line la = a.Lines[i], lb = b.Lines[i];
            if (la.AccountId != lb.AccountId) return false;
            if (la.Direction != lb.Direction) return false;
            if (la.Amount != lb.Amount) return false;
            if (!SameDimensions(la.Dimensions, lb.Dimensions)) return false;
        }

        return true;
    }

    private static bool SameDimensions(
        IReadOnlyDictionary<string, Guid> x, IReadOnlyDictionary<string, Guid> y)
    {
        if (x.Count != y.Count) return false;
        if (x.Count == 0) return true;
        foreach (KeyValuePair<string, Guid> kv in x)
            if (!y.TryGetValue(kv.Key, out Guid v) || v != kv.Value) return false;
        return true;
    }
}
