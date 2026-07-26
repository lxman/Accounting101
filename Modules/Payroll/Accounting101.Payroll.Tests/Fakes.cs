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

    // Maps a SourceRef to the request that first posted under it, and to the entry that request minted —
    // mirrors the real engine's idempotent-post arbitration (LedgerEndpoints.PostEntry /
    // EntryComparison.SameFinancialContent): a second PostAsync carrying the SAME SourceRef is either a
    // replay of the same operation (return the existing entry, don't double-count the fold) or a key
    // reused for different content (422 in production; an exception here).
    private readonly Dictionary<Guid, PostEntryRequest> _bySourceRef = new();
    private readonly Dictionary<Guid, Guid> _entryIdBySourceRef = new();

    // PostAsync is now the target of a per-SourceRef check-then-act over plain (non-concurrent)
    // collections, and the race tests in this project genuinely drive two racers through it
    // concurrently past their store-side rendezvous. A lock here is minimal and keeps this fake's
    // long-standing plain-Dictionary/List design everywhere else (see the 2026-07-26
    // toctou-status-code plan's Task 9 cross-cutting finding).
    private readonly object _postLock = new();

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>Each singular by-source-ref read, recorded so tests can assert detail reads pass through once.</summary>
    public List<Guid> SingularCalls { get; } = [];

    /// <summary>Each batched by-source-refs read, recorded so tests can assert a list folds in ONE call (not N+1).</summary>
    public List<IReadOnlyList<Guid>> BatchCalls { get; } = [];

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            if (entry.SourceRef is { } sourceRef && _bySourceRef.TryGetValue(sourceRef, out PostEntryRequest? seen))
            {
                // Production answers 422 when a key is reused for different content. Mirror that here, or every
                // divergent-retry test written against this fake passes vacuously.
                if (!SameContent(seen, entry))
                    throw new InvalidOperationException(
                        $"Entry for SourceRef {sourceRef} already exists with different content.");

                EntryResponse existing = _entries[_entryIdBySourceRef[sourceRef]];
                return Task.FromResult(new PostEntryResponse(existing.Id, existing.Status, existing.Posting));
            }

            _posted.Add(entry);
            var id = Guid.NewGuid();
            _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null);
            if (entry.SourceRef is { } sr)
            {
                _bySourceRef[sr] = entry;
                _entryIdBySourceRef[sr] = id;
            }
            return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
        }
    }

    // Matches EntryComparison.SameFinancialContent (Backend/Accounting101.Ledger.Core/Journal/
    // EntryComparison.cs) field-for-field, minus SourceRef (already guaranteed equal — the call site
    // above only reaches this after keying on a matching SourceRef). Reference/Memo are deliberately
    // NOT compared: production ignores them (EntryComparison.cs:5-7's own doc comment calls them
    // "descriptive", and the implementation never reads them), so a Memo/Reference-only retry is a
    // successful replay in production, not a 422 — comparing them here would make this fake STRICTER
    // than production, which is exactly the wrong direction for a fidelity fake.
    private static bool SameContent(PostEntryRequest a, PostEntryRequest b) =>
        a.EffectiveDate == b.EffectiveDate
        && a.Type == b.Type
        && a.SourceType == b.SourceType
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p =>
            p.First.AccountId == p.Second.AccountId
            && p.First.Direction == p.Second.Direction
            && p.First.Amount == p.Second.Amount
            && SameDimensions(p.First.Dimensions, p.Second.Dimensions));

    // Ported from EntryComparison.SameDimensions (EntryComparison.cs:37-45), a value comparison (not
    // reference equality) — extended only for PostLineRequest.Dimensions being nullable, unlike
    // production's non-null Line.Dimensions; null and empty are treated as equivalent.
    private static bool SameDimensions(IReadOnlyDictionary<string, Guid>? x, IReadOnlyDictionary<string, Guid>? y)
    {
        int xCount = x?.Count ?? 0;
        int yCount = y?.Count ?? 0;
        if (xCount != yCount) return false;
        if (xCount == 0) return true;
        foreach (KeyValuePair<string, Guid> kv in x!)
            if (!y!.TryGetValue(kv.Key, out Guid v) || v != kv.Value) return false;
        return true;
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

    public Task<(PayrollRun Run, bool Created)> RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        PayrollRun run = new()
        {
            Id = id,
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
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, run.Id), run);
        PayrollRun stored = created ? run : _store[(clientId, run.Id)];
        return Task.FromResult((stored, created));
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

    public Task<(TaxRemittance Remittance, bool Created)> RecordAsync(Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        TaxRemittance remittance = new()
        {
            Id = id,
            Number = $"TR-{Interlocked.Increment(ref _next):D5}",
            WithholdingsAmount = body.WithholdingsAmount,
            TaxesAmount = body.TaxesAmount,
            PayDate = body.PayDate,
            Memo = body.Memo,
            Status = TaxRemittanceStatus.Posted,
        };
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, remittance.Id), remittance);
        TaxRemittance stored = created ? remittance : _store[(clientId, remittance.Id)];
        return Task.FromResult((stored, created));
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
