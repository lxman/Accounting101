using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>The cash recipes: a disbursement or deposit composes into one balanced journal entry.
/// Pure — request in, wire DTO out — leaving sequencing, approval, and persistence to the engine.</summary>
public static class CashPosting
{
    public const string DisbursementSourceType = "CashDisbursement";
    public const string DepositSourceType = "CashDeposit";

    /// <summary>Composes the journal entry for a cash disbursement (Dr lines / Cr Cash).
    /// <para>Lines sharing an account collapse to one debit, ordered by account id for determinism.</para>
    /// <para>Throws <see cref="ArgumentException"/> when lines are empty, any amount is non-positive,
    /// or any line account equals the configured Cash account.</para>
    /// </summary>
    public static PostEntryRequest ComposeDisbursement(Guid id, CashDisbursementBody body, CashPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        ValidateLines(body.Lines, accounts.CashAccountId, nameof(body));

        // Dr each line's account — lines sharing an account collapse; ordered by account id for determinism.
        List<PostLineRequest> lines = body.Lines
            .GroupBy(line => line.AccountId)
            .OrderBy(group => group.Key)
            .Select(group => new PostLineRequest(group.Key, "Debit", group.Sum(line => line.Amount)))
            .ToList();

        decimal total = body.Lines.Sum(line => line.Amount);
        lines.Add(new PostLineRequest(accounts.CashAccountId, "Credit", total));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DisbursementSourceType, id),
            EffectiveDate: body.Date,
            Reference: body.Reference,
            Memo: body.Memo,
            Lines: lines,
            SourceRef: id,
            SourceType: DisbursementSourceType);
    }

    /// <summary>Composes the journal entry for a cash deposit (Dr Cash / Cr lines).
    /// <para>Lines sharing an account collapse to one credit, ordered by account id for determinism.</para>
    /// <para>Throws <see cref="ArgumentException"/> when lines are empty, any amount is non-positive,
    /// or any line account equals the configured Cash account.</para>
    /// </summary>
    public static PostEntryRequest ComposeDeposit(Guid id, CashDepositBody body, CashPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        ValidateLines(body.Lines, accounts.CashAccountId, nameof(body));

        decimal total = body.Lines.Sum(line => line.Amount);
        List<PostLineRequest> lines = [new PostLineRequest(accounts.CashAccountId, "Debit", total)];

        // Cr each line's account — lines sharing an account collapse; ordered by account id for determinism.
        lines.AddRange(body.Lines
            .GroupBy(line => line.AccountId)
            .OrderBy(group => group.Key)
            .Select(group => new PostLineRequest(group.Key, "Credit", group.Sum(line => line.Amount))));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DepositSourceType, id),
            EffectiveDate: body.Date,
            Reference: body.Reference,
            Memo: body.Memo,
            Lines: lines,
            SourceRef: id,
            SourceType: DepositSourceType);
    }

    private static void ValidateLines(IReadOnlyList<CashLine> lines, Guid cashAccountId, string paramName)
    {
        if (lines.Count == 0)
            throw new ArgumentException("lines must not be empty", paramName);

        foreach (CashLine line in lines)
        {
            if (line.Amount <= 0m)
                throw new ArgumentException(
                    $"every line amount must be positive; got {line.Amount} for account {line.AccountId}",
                    paramName);

            if (line.AccountId == cashAccountId)
                throw new ArgumentException(
                    $"line account {line.AccountId} must not equal the configured Cash account",
                    paramName);
        }
    }
}
