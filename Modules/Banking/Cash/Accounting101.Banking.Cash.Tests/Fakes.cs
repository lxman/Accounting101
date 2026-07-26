using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// An in-memory stand-in for the ledger engine: records what was posted, models approve/reverse/void, and
/// resolves entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
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
    // collections, and the race tests in this project (CashRaceTests) genuinely drive two racers
    // through it concurrently past their store-side rendezvous. A lock here is minimal and keeps this
    // fake's long-standing plain-Dictionary/List design everywhere else (see the 2026-07-26
    // toctou-status-code plan's Task 9 cross-cutting finding).
    //
    // The SAME lock also guards every OTHER member that reads or mutates _entries/_posted below
    // (ReverseAsync, VoidAsync, GetEntriesBySourceRefAsync, GetEntriesBySourceRefsAsync, the Posted
    // getter) — not just PostAsync's write path. This fake predates concurrent access entirely (it was
    // built as a single-threaded, sequential test double); CashRaceTests's racers are the first callers
    // to genuinely drive a reader concurrently with PostAsync's writer, which would throw
    // InvalidOperationException on a plain Dictionary enumerated mid-insert. Cash is not exposed by any
    // test today (nothing between its barrier and PostAsync touches the ledger), but the lock is added
    // for consistency — the next test added here will not know which reads are safe. `lock` is
    // reentrant on the same thread, and none of these methods call another locked member on `this` or
    // await while holding the lock (all bodies below are synchronous despite the Task-returning
    // signature — verified before adding these locks), so there is no deadlock risk.
    private readonly object _postLock = new();

    public IReadOnlyList<PostEntryRequest> Posted
    {
        get { lock (_postLock) return _posted.ToList(); }
    }

    /// <summary>Each singular by-source-ref read, recorded so tests can assert detail reads pass through once.</summary>
    public List<Guid> SingularCalls { get; } = [];

    /// <summary>Each batched by-source-refs read, recorded so tests can assert a list folds in ONE call (not N+1).</summary>
    public List<IReadOnlyList<Guid>> BatchCalls { get; } = [];

    // FORWARD-LOOKING CAVEAT (2026-07-26): a same-SourceRef replay returns the FIRST request's
    // persisted entry without appending to _posted a second time — already true before this fix, and
    // no existing assertion depends on the opposite. But it means a future "a replay posts no second
    // entry" test written against THIS fake would pass whether or not the service under test actually
    // replays correctly, because the fake collapses the duplicate for you regardless. Assert that
    // property against the real engine (HTTP/Mongo), not this fake.
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
        lock (_postLock)
        {
            EntryResponse original = _entries[entryId];
            var id = Guid.NewGuid();
            EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId);
            _entries[id] = reversal;
            return Task.FromResult(reversal);
        }
    }

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            EntryResponse voided = _entries[entryId] with { Status = "Voided" };
            _entries[entryId] = voided;
            return Task.FromResult(voided);
        }
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            SingularCalls.Add(sourceRef);
            return Task.FromResult<IReadOnlyList<EntryResponse>>(_entries.Values.Where(e => e.SourceRef == sourceRef).ToList());
        }
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            BatchCalls.Add(sourceRefs);
            return Task.FromResult<IReadOnlyList<EntryResponse>>(
                _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());
        }
    }

    public Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

    private static EntryResponse Entry(Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf) =>
        new(id, 0, default, "Standard", "Active", posting, 0, null, null, reversalOf, null, [], sourceRef, sourceType);
}

internal sealed class InMemoryCashDisbursementStore : ICashDisbursementStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), CashDisbursement> _store = new();
    private int _next;

    public Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        CashDisbursement doc = new()
        {
            Id = id,
            Number = $"CD-{Interlocked.Increment(ref _next):D5}",
            Lines = body.Lines,
            Date = body.Date,
            Reference = body.Reference,
            Memo = body.Memo,
            Status = CashDisbursementStatus.Posted,
        };
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, doc.Id), doc);
        CashDisbursement stored = created ? doc : _store[(clientId, doc.Id)];
        return Task.FromResult((stored, created));
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        _store[(clientId, id)] = _store[(clientId, id)] with { Status = CashDisbursementStatus.Void };
        return Task.CompletedTask;
    }

    public Task<CashDisbursement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));

    public Task<IReadOnlyList<CashDisbursement>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CashDisbursement>>(
            _store.Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value).ToList());

    public Task<PagedResponse<CashDisbursement>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<CashDisbursement> all = _store
            .Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value)
            .Where(d => includeVoided || d.Status != CashDisbursementStatus.Void);
        List<CashDisbursement> ordered = (descending ? all.OrderByDescending(d => d.Number) : all.OrderBy(d => d.Number)).ToList();
        List<CashDisbursement> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<CashDisbursement>(items, ordered.Count, skip, limit));
    }
}

internal sealed class InMemoryCashDepositStore : ICashDepositStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), CashDeposit> _store = new();
    private int _next;

    public Task<(CashDeposit Deposit, bool Created)> RecordAsync(Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        CashDeposit doc = new()
        {
            Id = id,
            Number = $"CR-{Interlocked.Increment(ref _next):D5}",
            Lines = body.Lines,
            Date = body.Date,
            Reference = body.Reference,
            Memo = body.Memo,
            Status = CashDepositStatus.Posted,
        };
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, doc.Id), doc);
        CashDeposit stored = created ? doc : _store[(clientId, doc.Id)];
        return Task.FromResult((stored, created));
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        _store[(clientId, id)] = _store[(clientId, id)] with { Status = CashDepositStatus.Void };
        return Task.CompletedTask;
    }

    public Task<CashDeposit?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));

    public Task<IReadOnlyList<CashDeposit>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CashDeposit>>(
            _store.Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value).ToList());

    public Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<CashDeposit> all = _store
            .Where(kv => kv.Key.Item1 == clientId).Select(kv => kv.Value)
            .Where(d => includeVoided || d.Status != CashDepositStatus.Void);
        List<CashDeposit> ordered = (descending ? all.OrderByDescending(d => d.Number) : all.OrderBy(d => d.Number)).ToList();
        List<CashDeposit> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<CashDeposit>(items, ordered.Count, skip, limit));
    }
}

internal sealed class FixedCashAccountsProvider(CashPostingAccounts fixedAccounts) : ICashAccountsProvider
{
    public Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(fixedAccounts);
}
