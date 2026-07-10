using System.Globalization;
using Accounting101.Ledger.Core.Accounts;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Coordinates chart-of-accounts changes so they are accountable. An account is reference data (mutable,
/// not append-only), but a change to it — create, rename, retype, deactivate — is control-relevant, so it
/// is recorded on the client's tamper-evident audit chain with the actor and a before/after summary. The
/// replace and the audit append commit together in one transaction, so a change can never land unaudited.
/// </summary>
public sealed class ChartService
{
    private readonly IMongoClient _client;
    private readonly MongoAccountStore _accounts;
    private readonly MongoAuditLog _audit;

    public ChartService(IMongoClient client, MongoAccountStore accounts, MongoAuditLog audit)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task UpsertAsync(Account account, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(actor);

        Account? prior = await _accounts.GetAsync(account.Id, cancellationToken);
        AuditAction action = prior is null ? AuditAction.AccountCreated : AuditAction.AccountUpdated;
        string summary = prior is null ? Describe(account) : Diff(prior, account);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using IClientSessionHandle session = await _client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (s, _) =>
            {
                await _accounts.UpsertAsync(account, s, cancellationToken);
                await _audit.AppendAsync(account.ClientId, account.Id, 0, action, actor, summary, now, s, cancellationToken);
                return true;
            },
            cancellationToken: cancellationToken);
    }

    private static string Describe(Account a) =>
        $"Created {a.Number} \"{a.Name}\" ({a.Type})"
        + (a.RequiredDimensions.Count > 0 ? $", requires {string.Join(", ", a.RequiredDimensions)}" : "")
        + (a.IsRetainedEarnings ? ", retained-earnings" : "")
        + (a.Postable ? "" : ", summary")
        + (a.Active ? "" : ", inactive");

    /// <summary>A concise "field: old → new" of what changed; for the auditor to see exactly what was altered.</summary>
    private static string Diff(Account before, Account after)
    {
        List<string> changes = [];
        Compare(changes, "Number", before.Number, after.Number);
        Compare(changes, "Name", before.Name, after.Name);
        Compare(changes, "Type", before.Type, after.Type);
        Compare(changes, "ParentId", before.ParentId, after.ParentId);
        Compare(changes, "Postable", before.Postable, after.Postable);
        CompareDimensions(changes, "RequiredDimensions", before.RequiredDimensions, after.RequiredDimensions);
        Compare(changes, "CashFlowActivity", before.CashFlowActivity, after.CashFlowActivity);
        Compare(changes, "IsRetainedEarnings", before.IsRetainedEarnings, after.IsRetainedEarnings);
        Compare(changes, "Active", before.Active, after.Active);
        return changes.Count == 0 ? "No field changes." : string.Join("; ", changes);
    }

    /// <summary>Compares the full required-dimension set (order-insensitive — it's a set of dimension types,
    /// not a sequence), not just the legacy first-or-null value, so an added/removed/reordered dimension on
    /// a multi-dimension control account is never silently dropped from the audit trail.</summary>
    private static void CompareDimensions(List<string> changes, string field, IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        if (before.ToHashSet().SetEquals(after))
            return;
        changes.Add($"{field}: {ShowSet(before)} → {ShowSet(after)}");
    }

    private static string ShowSet(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private static void Compare<T>(List<string> changes, string field, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            changes.Add($"{field}: {Show(before)} → {Show(after)}");
    }

    private static string Show(object? value) =>
        value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
