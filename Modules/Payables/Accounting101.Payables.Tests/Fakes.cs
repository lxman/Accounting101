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
    private readonly Dictionary<Guid, IReadOnlyList<PostLineRequest>> _linesById = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    /// <summary>
    /// Optional hook: tests set this to drive the validation outcome. When null (the default), validation
    /// succeeds silently. Set to a delegate that throws <see cref="LedgerClientException"/> to simulate a
    /// rejection (e.g. a closed-period 409) without HTTP.
    /// </summary>
    public Func<PostEntryRequest, Task>? OnValidate { get; set; }

    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        if (OnValidate is not null)
            await OnValidate(entry);
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
        // A reversal's fold effect is the original entry's lines with direction flipped — the original
        // entry stays Active (and still counted) so the pair nets to zero, exactly like the real engine.
        IReadOnlyList<PostLineRequest> reversedLines = _linesById.TryGetValue(entryId, out IReadOnlyList<PostLineRequest>? originalLines)
            ? originalLines.Select(l => l with { Direction = Flip(l.Direction) }).ToList()
            : [];
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId, lines: reversedLines);
        _entries[id] = reversal;
        _linesById[id] = reversedLines;
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

    /// <summary>
    /// A real per-dimension fold over every posted entry's lines, mirroring the real engine's subledger
    /// (debit-positive, grouped by dimension value) closely enough to drive BillPaymentService/BillService
    /// under unit tests. Unlike the real engine, this fold does NOT gate on approval (Posting == "Posted") —
    /// it counts every Active entry regardless of approval state, matching this fake's long-standing
    /// "immediate effect" semantics (these unit tests never approve most entries). The approval-gated fold
    /// behavior is proven separately by the real HTTP-backed engine tests (SubledgerReadTests, PayablesE2eTests).
    /// Voided entries are excluded; a reversed entry stays Active and its reversal's negated lines net it to
    /// zero, same as production. <paramref name="includePending"/> is accepted so this fake compiles against
    /// the <see cref="ILedgerClient"/> contract, but is a no-op here — the fold already counts every Active
    /// entry regardless of Posting, in either mode.
    /// </summary>
    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        Dictionary<Guid, decimal> totals = new();
        foreach ((Guid id, EntryResponse response) in _entries)
        {
            if (response.Status != "Active") continue;
            if (!_linesById.TryGetValue(id, out IReadOnlyList<PostLineRequest>? lines)) continue;
            foreach (PostLineRequest line in lines)
            {
                if (line.AccountId != account) continue;
                if (line.Dimensions is null || !line.Dimensions.TryGetValue(dimension, out Guid dimValue)) continue;
                decimal signed = line.Direction == "Debit" ? line.Amount : -line.Amount;
                totals[dimValue] = totals.GetValueOrDefault(dimValue) + signed;
            }
        }
        return Task.FromResult<IReadOnlyList<SubledgerLineResponse>>(
            totals.Select(kv => new SubledgerLineResponse(account, kv.Key, kv.Value)).ToList());
    }

    private static string Flip(string direction) => direction == "Debit" ? "Credit" : "Debit";

    private static EntryResponse Entry(
        Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf,
        IReadOnlyList<PostLineRequest>? lines = null)
    {
        IReadOnlyList<EntryLineResponse> mapped = (lines ?? []).Select(l =>
            new EntryLineResponse(l.AccountId, l.Direction, l.Amount, l.Dimensions ?? new Dictionary<string, Guid>(), null)).ToList();
        return new(id, 0, default, "Standard", "Active", posting, mapped.Count, null, null, reversalOf, null, mapped, sourceRef, sourceType);
    }
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

    public Task<IReadOnlyList<Vendor>> ListAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Vendor>>(
            _store.Where(kv => kv.Key.Item1 == clientId)
                .Select(kv => kv.Value)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
}

internal sealed class InMemoryBillStore : IBillStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Bill> _drafts = new();
    private readonly ConcurrentDictionary<(Guid, Guid), Bill> _entered = new();
    private int _next;

    public Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Bill draft = new()
        {
            Id = Guid.NewGuid(), VendorId = body.VendorId, Number = null,
            BillDate = body.BillDate, DueDate = body.DueDate,
            VendorReference = body.VendorReference, Memo = body.Memo, Status = BillStatus.Draft,
            Lines = body.Lines.Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId }).ToList(),
        };
        _drafts[(clientId, draft.Id)] = draft;
        return Task.FromResult(draft);
    }

    public Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        if (!_drafts.ContainsKey((clientId, billId)))
            throw new InvalidOperationException($"Bill {billId} is not an editable draft.");
        Bill updated = new()
        {
            Id = billId, VendorId = body.VendorId, Number = null,
            BillDate = body.BillDate, DueDate = body.DueDate,
            VendorReference = body.VendorReference, Memo = body.Memo, Status = BillStatus.Draft,
            Lines = body.Lines.Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId }).ToList(),
        };
        _drafts[(clientId, billId)] = updated;
        return Task.FromResult(updated);
    }

    public Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (!_drafts.TryRemove((clientId, billId), out _))
            throw new InvalidOperationException($"Bill {billId} is not a discardable draft.");
        return Task.CompletedTask;
    }

    public Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (!_drafts.TryRemove((clientId, billId), out Bill? draft))
            throw new InvalidOperationException($"Bill {billId} is not a draft awaiting enter.");
        Guid enteredId = Guid.NewGuid();
        Bill entered = draft with { Id = enteredId, Number = $"BILL-{Interlocked.Increment(ref _next):D5}", Status = BillStatus.Entered };
        _entered[(clientId, enteredId)] = entered;
        return Task.FromResult(entered);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (_entered.TryGetValue((clientId, billId), out Bill? b))
            _entered[(clientId, billId)] = b with { Status = BillStatus.Void };
        return Task.CompletedTask;
    }

    public Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (_drafts.TryGetValue((clientId, billId), out Bill? draft))
            return Task.FromResult<Bill?>(draft);
        return Task.FromResult(_entered.GetValueOrDefault((clientId, billId)));
    }

    /// <summary>Returns ALL bills (drafts + entered, including voided) — the service filters itself.</summary>
    public Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IEnumerable<Bill> drafts = _drafts.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId).Select(kv => kv.Value);
        IEnumerable<Bill> entered = _entered.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId).Select(kv => kv.Value);
        return Task.FromResult<IReadOnlyList<Bill>>(drafts.Concat(entered).ToList());
    }
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
