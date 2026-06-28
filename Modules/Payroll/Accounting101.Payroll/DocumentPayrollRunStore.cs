using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>
/// Persists payroll runs through the engine's document store as <em>evidentiary</em> data: a run is
/// created mutable (draft), immediately finalized into an append-only numbered document, and voidable.
/// Number and status are derived from the engine's envelope, never stored. The module owns no
/// database connection.
/// </summary>
public sealed class DocumentPayrollRunStore(IDocumentStore documents) : IPayrollRunStore
{
    private const string Collection = "payroll-runs";

    public async Task<PayrollRun> RecordAsync(Guid clientId, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create draft then immediately finalize to assign the sequence number.
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<PayrollRunBody>? result = await documents.GetAsync<PayrollRunBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, runId, ct);

    public async Task<PayrollRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        DocumentResult<PayrollRunBody>? result = await documents.GetAsync<PayrollRunBody>(clientId, Collection, runId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<PayrollRun>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<PayrollRunBody>> results =
            await documents.QueryAsync<PayrollRunBody>(clientId, Collection, Tags(), cancellationToken: ct);
        return results.Select(Map).ToList();
    }

    public async Task<PagedResponse<PayrollRun>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<PayrollRunBody>> page =
            await documents.QueryAsync<PayrollRunBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<PayrollRun>(page.Select(Map).ToList(), total, skip, limit);
    }

    private static Dictionary<string, string> Tags() => new();

    private static PayrollRun Map(DocumentResult<PayrollRunBody> result) => new()
    {
        Id = result.Id,
        Number = result.Sequence is { } seq ? $"PR-{seq:D5}" : null,
        Gross = result.Body.Gross,
        EmployeeFica = result.Body.EmployeeFica,
        EmployerFica = result.Body.EmployerFica,
        Deductions = result.Body.Deductions,
        IncomeTaxWithheld = result.Body.IncomeTaxWithheld,
        PayDate = result.Body.PayDate,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => PayrollRunStatus.Void,
            _ => PayrollRunStatus.Posted,
        },
    };
}
