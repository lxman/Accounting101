using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
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

    public Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
    {
        EntryResponse posted = _entries[entryId] with { Posting = "Posted" };
        _entries[entryId] = posted;
        return Task.FromResult(posted);
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

internal sealed class InMemoryVendorStore : IVendorStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Vendor> _store = new();

    public Task SaveAsync(Guid clientId, Vendor vendor, CancellationToken ct = default)
    {
        _store[(clientId, vendor.Id)] = vendor;
        return Task.CompletedTask;
    }

    public Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, vendorId)));
}

internal sealed class InMemoryBillStore : IBillStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Bill> _store = new();
    private int _next;

    public Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Bill draft = new()
        {
            Id = Guid.NewGuid(),
            VendorId = body.VendorId,
            Number = null,
            BillDate = body.BillDate,
            DueDate = body.DueDate,
            VendorReference = body.VendorReference,
            Memo = body.Memo,
            Status = BillStatus.Draft,
            Lines = body.Lines.Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId }).ToList(),
        };
        _store[(clientId, draft.Id)] = draft;
        return Task.FromResult(draft);
    }

    public Task<Bill> FinalizeAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill draft = _store[(clientId, billId)];
        Bill entered = draft with { Number = $"BILL-{Interlocked.Increment(ref _next):D5}", Status = BillStatus.Entered };
        _store[(clientId, billId)] = entered;
        return Task.FromResult(entered);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        _store[(clientId, billId)] = _store[(clientId, billId)] with { Status = BillStatus.Void };
        return Task.CompletedTask;
    }

    public Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, billId)));

    /// <summary>Returns ALL bills including voided — the service filters voided itself.</summary>
    public Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Bill>>(
            _store.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId)
                .Select(kv => kv.Value).ToList());
}

internal sealed class InMemoryBillPaymentStore : IBillPaymentStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), BillPayment> _payments = new();
    private readonly ConcurrentDictionary<(Guid, Guid), VendorCreditApplication> _credits = new();

    public Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        BillPayment p = new()
        {
            Id = Guid.NewGuid(), VendorId = body.VendorId, Date = body.Date, Amount = body.Amount,
            Method = body.Method, Allocations = body.Allocations, Voided = false,
        };
        _payments[(clientId, p.Id)] = p;
        return Task.FromResult(p);
    }

    public Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        VendorCreditApplication c = new()
        {
            Id = Guid.NewGuid(), VendorId = body.VendorId, Date = body.Date,
            Allocations = body.Allocations, Voided = false,
        };
        _credits[(clientId, c.Id)] = c;
        return Task.FromResult(c);
    }

    public Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        if (_payments.TryGetValue((clientId, paymentId), out BillPayment? p))
            _payments[(clientId, paymentId)] = p with { Voided = true };
        return Task.CompletedTask;
    }

    public Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        Task.FromResult(_payments.GetValueOrDefault((clientId, paymentId)));

    /// <summary>Returns ALL payments including voided — the service filters !Voided itself.</summary>
    public Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BillPayment>>(
            _payments.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId)
                .Select(kv => kv.Value).ToList());

    public Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorCreditApplication>>(
            _credits.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId)
                .Select(kv => kv.Value).ToList());
}
