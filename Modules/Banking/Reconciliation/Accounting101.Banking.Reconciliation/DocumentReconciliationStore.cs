using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists reconciliations as plain (editable) documents: created with an empty cleared set and a
/// counter-assigned number, overwritten as the cleared set changes, read back by id.</summary>
public sealed class DocumentReconciliationStore(IDocumentStore documents) : IReconciliationStore
{
    private const string Collection = "reconciliations";

    public async Task<Reconciliation> CreateAsync(
        Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default)
    {
        long n = await documents.NextNumberAsync(clientId, "reconciliation", ct);
        Reconciliation reconciliation = new()
        {
            Id = Guid.NewGuid(),
            Number = $"REC-{n:D5}",
            CashAccountId = cashAccountId,
            BankStatementId = bankStatementId,
            StatementDate = statementDate,
            Status = ReconciliationStatus.InProgress,
            ClearedEntryIds = [],
        };
        await documents.PutAsync(clientId, Collection, reconciliation.Id, reconciliation, Tags(), ct);
        return reconciliation;
    }

    public Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        return documents.PutAsync(clientId, Collection, reconciliation.Id, reconciliation, Tags(), ct);
    }

    public async Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<Reconciliation>? result = await documents.GetAsync<Reconciliation>(clientId, Collection, id, ct);
        return result?.Body;
    }

    private static Dictionary<string, string> Tags() => new();
}
