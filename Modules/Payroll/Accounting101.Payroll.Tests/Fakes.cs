using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
/// </summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>Each singular by-source-ref read, recorded so tests can assert detail reads pass through once.</summary>
    public List<Guid> SingularCalls { get; } = [];

    /// <summary>Each batched by-source-refs read, recorded so tests can assert a list folds in ONE call (not N+1).</summary>
    public List<IReadOnlyList<Guid>> BatchCalls { get; } = [];

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null);
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId);
        _entries[id] = reversal;
        return Task.FromResult(reversal);
    }

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        EntryResponse voided = _entries[entryId] with { Status = "Voided" };
        _entries[entryId] = voided;
        return Task.FromResult(voided);
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        SingularCalls.Add(sourceRef);
        return Task.FromResult<IReadOnlyList<EntryResponse>>(_entries.Values.Where(e => e.SourceRef == sourceRef).ToList());
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        BatchCalls.Add(sourceRefs);
        return Task.FromResult<IReadOnlyList<EntryResponse>>(
            _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());
    }

    public Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

    private static EntryResponse Entry(Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf) =>
        new(id, 0, default, "Standard", "Active", posting, 0, null, null, reversalOf, null, [], sourceRef, sourceType);
}

internal sealed class InMemoryPayrollRunStore : IPayrollRunStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), PayrollRun> _store = new();
    private int _next;

    public Task<PayrollRun> RecordAsync(Guid clientId, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        PayrollRun run = new()
        {
            Id = Guid.NewGuid(),
            Number = $"PR-{Interlocked.Increment(ref _next):D5}",
            Gross = body.Gross,
            EmployeeFica = body.EmployeeFica,
            EmployerFica = body.EmployerFica,
            Deductions = body.Deductions,
            IncomeTaxWithheld = body.IncomeTaxWithheld,
            PayDate = body.PayDate,
            Memo = body.Memo,
            Status = PayrollRunStatus.Posted,
        };
        _store[(clientId, run.Id)] = run;
        return Task.FromResult(run);
    }

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        _store[(clientId, runId)] = _store[(clientId, runId)] with { Status = PayrollRunStatus.Void };
        return Task.CompletedTask;
    }

    public Task<PayrollRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, runId)));

    public Task<IReadOnlyList<PayrollRun>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PayrollRun>>(
            _store.Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value).ToList());

    public Task<PagedResponse<PayrollRun>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<PayrollRun> all = _store
            .Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value)
            .Where(r => includeVoided || r.Status != PayrollRunStatus.Void);
        List<PayrollRun> ordered = (descending ? all.OrderByDescending(r => r.Number) : all.OrderBy(r => r.Number)).ToList();
        List<PayrollRun> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<PayrollRun>(items, ordered.Count, skip, limit));
    }
}

internal sealed class InMemoryTaxRemittanceStore : ITaxRemittanceStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), TaxRemittance> _store = new();
    private int _next;

    public Task<TaxRemittance> RecordAsync(Guid clientId, TaxRemittanceBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        TaxRemittance remittance = new()
        {
            Id = Guid.NewGuid(),
            Number = $"TR-{Interlocked.Increment(ref _next):D5}",
            WithholdingsAmount = body.WithholdingsAmount,
            TaxesAmount = body.TaxesAmount,
            PayDate = body.PayDate,
            Memo = body.Memo,
            Status = TaxRemittanceStatus.Posted,
        };
        _store[(clientId, remittance.Id)] = remittance;
        return Task.FromResult(remittance);
    }

    public Task VoidAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default)
    {
        _store[(clientId, remittanceId)] = _store[(clientId, remittanceId)] with { Status = TaxRemittanceStatus.Void };
        return Task.CompletedTask;
    }

    public Task<TaxRemittance?> GetAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, remittanceId)));

    public Task<IReadOnlyList<TaxRemittance>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaxRemittance>>(
            _store.Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value).ToList());

    public Task<PagedResponse<TaxRemittance>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<TaxRemittance> all = _store
            .Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value)
            .Where(r => includeVoided || r.Status != TaxRemittanceStatus.Void);
        List<TaxRemittance> ordered = (descending ? all.OrderByDescending(r => r.Number) : all.OrderBy(r => r.Number)).ToList();
        List<TaxRemittance> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<TaxRemittance>(items, ordered.Count, skip, limit));
    }
}

internal sealed class FixedPayrollAccountsProvider(PayrollPostingAccounts fixedAccounts) : IPayrollAccountsProvider
{
    public Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(fixedAccounts);
}
