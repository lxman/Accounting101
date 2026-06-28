using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists bank statements through the engine's document store as evidentiary data: created
/// mutable then immediately finalized into an append-only numbered document. Number/status derived from
/// the envelope, never stored.</summary>
public sealed class DocumentBankStatementStore(IDocumentStore documents) : IBankStatementStore
{
    private const string Collection = "bank-statements";

    public async Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankStatementBody>? result = await documents.GetAsync<BankStatementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public async Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<BankStatementBody>? result = await documents.GetAsync<BankStatementBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BankStatementBody>> results =
            await documents.QueryAsync<BankStatementBody>(clientId, Collection, Tags(), ct);
        return results.Where(r => r.Body.CashAccountId == cashAccountId).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static BankStatement Map(DocumentResult<BankStatementBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"BST-{seq:D5}" : null,
        CashAccountId = r.Body.CashAccountId,
        StatementDate = r.Body.StatementDate,
        OpeningBalance = r.Body.OpeningBalance,
        ClosingBalance = r.Body.ClosingBalance,
        Lines = r.Body.Lines,
        Status = r.State is DocumentLifecycle.Voided or DocumentLifecycle.Superseded ? BankStatementStatus.Void : BankStatementStatus.Posted,
    };
}
