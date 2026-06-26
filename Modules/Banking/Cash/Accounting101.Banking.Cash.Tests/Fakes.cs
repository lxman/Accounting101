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

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

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

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(_entries.Values.Where(e => e.SourceRef == sourceRef).ToList());

    private static EntryResponse Entry(Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf) =>
        new(id, 0, default, "Standard", "Active", posting, 0, null, null, reversalOf, null, [], sourceRef, sourceType);
}

internal sealed class InMemoryCashDisbursementStore : ICashDisbursementStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), CashDisbursement> _store = new();
    private int _next;

    public Task<CashDisbursement> RecordAsync(Guid clientId, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        CashDisbursement doc = new()
        {
            Id = Guid.NewGuid(),
            Number = $"CD-{Interlocked.Increment(ref _next):D5}",
            Lines = body.Lines,
            Date = body.Date,
            Reference = body.Reference,
            Memo = body.Memo,
            Status = CashDisbursementStatus.Posted,
        };
        _store[(clientId, doc.Id)] = doc;
        return Task.FromResult(doc);
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
}

internal sealed class InMemoryCashDepositStore : ICashDepositStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), CashDeposit> _store = new();
    private int _next;

    public Task<CashDeposit> RecordAsync(Guid clientId, CashDepositBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        CashDeposit doc = new()
        {
            Id = Guid.NewGuid(),
            Number = $"CR-{Interlocked.Increment(ref _next):D5}",
            Lines = body.Lines,
            Date = body.Date,
            Reference = body.Reference,
            Memo = body.Memo,
            Status = CashDepositStatus.Posted,
        };
        _store[(clientId, doc.Id)] = doc;
        return Task.FromResult(doc);
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
}

internal sealed class FixedCashAccountsProvider(CashPostingAccounts fixedAccounts) : ICashAccountsProvider
{
    public Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(fixedAccounts);
}
