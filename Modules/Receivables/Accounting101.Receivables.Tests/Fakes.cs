using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

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

    /// <summary>The most recently posted entry, or null if nothing has posted yet.</summary>
    public PostEntryRequest? LastPosted { get; private set; }

    /// <summary>Number of times <see cref="ReverseAsync"/> has been called — used by Posted→Reverse branch tests.</summary>
    public int ReversalCount { get; private set; }

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

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        LastPosted = entry;
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    /// <summary>
    /// Seed a Posted (on-the-books) entry directly into the fold WITHOUT recording it in <see cref="Posted"/>/
    /// <see cref="LastPosted"/>. For tests to establish pre-existing ledger state — e.g. an invoice's own
    /// AR-debit line, which InvoiceService posts in production but is out of scope for a PaymentService-only
    /// unit-test harness — without polluting assertions against what the code under test itself posted.
    /// </summary>
    public void SeedEntry(PostEntryRequest entry)
    {
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "Posted", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
    }

    public Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
    {
        EntryResponse posted = _entries[entryId] with { Posting = "Posted" };
        _entries[entryId] = posted;
        return Task.FromResult(posted);
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversalCount++;
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
    /// (debit-positive, grouped by dimension value) closely enough to drive PaymentService/CustomerAccountService
    /// under unit tests. Unlike the real engine, this fold does NOT gate on approval (Posting == "Posted") —
    /// it counts every Active entry regardless of approval state. That intentionally preserves this fake's
    /// long-standing "immediate effect" semantics (these unit tests never approve most entries); the
    /// approval-gated fold behavior is proven separately by the real HTTP-backed engine tests
    /// (SubledgerReadTests, PaymentDimensionTests, DispositionDimensionTests, FoldReadTests). Voided entries
    /// are excluded; a reversed entry stays Active and its reversal's negated lines net it to zero, same as
    /// production.
    /// <para>
    /// FIDELITY NOTE (2026-07-09, over-application fix): the caller-facing <paramref name="includePending"/>
    /// parameter was added so this fake compiles against the widened <see cref="ILedgerClient"/> contract,
    /// but it is a no-op here — the fold already counts every Active entry regardless of Posting, in either
    /// mode. Tightening the DEFAULT path to Posted-only (matching production exactly) broke 18 pre-existing
    /// PaymentService/CustomerAccountService unit tests that assert on payments/dispositions taking
    /// immediate effect without an explicit approve step. Rather than rewrite that whole suite (out of
    /// scope for this fix and not requested), the fake keeps its established "immediate effect" behavior.
    /// The two behaviors this fix actually depends on — pending reliefs reserving against an invoice for
    /// validation, and reads defaulting an unapproved invoice to fully open — are proven for real against
    /// the real engine by the HTTP E2E tests in ReceivablesLedgerFirstEdgeTests (over-application) and the
    /// invoice-read test for Finding 2, not by this fake.
    /// </para>
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

internal sealed class InMemoryCustomerStore : ICustomerStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Customer> _store = new();

    public Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default)
    {
        _store[(clientId, customer.Id)] = customer;
        return Task.CompletedTask;
    }

    public Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, customerId)));

    public Task<IReadOnlyList<Customer>> ListAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Customer>>(
            _store.Where(kv => kv.Key.Item1 == clientId)
                  .Select(kv => kv.Value)
                  .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                  .ToList());
}

internal sealed class InMemoryInvoiceStore : IInvoiceStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Invoice> _drafts = new();
    private readonly ConcurrentDictionary<(Guid, Guid), Invoice> _issued = new();
    private int _next;

    public Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default)
    {
        Invoice draft = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = body.CustomerId,
            Number = null,
            IssueDate = body.IssueDate,
            DueDate = body.DueDate,
            Status = InvoiceStatus.Draft,
            TaxRate = body.TaxRate,
            Memo = body.Memo,
            Lines = body.Lines.Select(l => new InvoiceLine { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Taxable = l.Taxable, RevenueCategory = l.RevenueCategory }).ToList(),
        };
        _drafts[(clientId, draft.Id)] = draft;
        return Task.FromResult(draft);
    }

    public Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CancellationToken cancellationToken = default)
    {
        if (!_drafts.ContainsKey((clientId, invoiceId)))
            throw new InvalidOperationException($"Invoice {invoiceId} is not an editable draft.");
        Invoice updated = new()
        {
            Id = invoiceId,
            CustomerId = body.CustomerId,
            Number = null,
            IssueDate = body.IssueDate,
            DueDate = body.DueDate,
            Status = InvoiceStatus.Draft,
            TaxRate = body.TaxRate,
            Memo = body.Memo,
            Lines = body.Lines.Select(l => new InvoiceLine { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Taxable = l.Taxable, RevenueCategory = l.RevenueCategory }).ToList(),
        };
        _drafts[(clientId, invoiceId)] = updated;
        return Task.FromResult(updated);
    }

    public Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (!_drafts.TryRemove((clientId, invoiceId), out _))
            throw new InvalidOperationException($"Invoice {invoiceId} is not a discardable draft.");
        return Task.CompletedTask;
    }

    public Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (!_drafts.TryRemove((clientId, invoiceId), out Invoice? draft))
            throw new InvalidOperationException($"Invoice {invoiceId} is not a draft awaiting issue.");
        Guid issuedId = Guid.NewGuid();
        Invoice issued = draft with
        {
            Id = issuedId,
            Number = $"INV-{Interlocked.Increment(ref _next):D5}",
            Status = InvoiceStatus.Issued,
        };
        _issued[(clientId, issuedId)] = issued;
        return Task.FromResult(issued);
    }

    public Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (_issued.TryGetValue((clientId, invoiceId), out Invoice? inv))
            _issued[(clientId, invoiceId)] = inv with { Status = InvoiceStatus.Void };
        return Task.CompletedTask;
    }

    public Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (_drafts.TryGetValue((clientId, invoiceId), out Invoice? draft))
            return Task.FromResult<Invoice?>(draft);
        return Task.FromResult(_issued.GetValueOrDefault((clientId, invoiceId)));
    }

    public Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default)
    {
        IEnumerable<Invoice> drafts = _drafts
            .Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId)
            .Select(kv => kv.Value);
        IEnumerable<Invoice> issued = _issued
            .Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId)
            .Select(kv => kv.Value);
        return Task.FromResult<IReadOnlyList<Invoice>>(drafts.Concat(issued).ToList());
    }
}

internal sealed class FixedAccountsProvider(InvoicePostingAccounts accounts) : IInvoiceAccountsProvider
{
    public Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accounts);
}

internal sealed class InMemoryPaymentStore : IPaymentStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Payment> _payments = new();
    private readonly ConcurrentDictionary<(Guid, Guid), CreditApplication> _credits = new();
    private readonly ConcurrentDictionary<(Guid, Guid), WriteOff> _writeOffs = new();
    private readonly ConcurrentDictionary<(Guid, Guid), CreditNote> _creditNotes = new();
    private readonly ConcurrentDictionary<(Guid, Guid), Refund> _refunds = new();

    public Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        Payment p = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date, Amount = body.Amount,
            Method = body.Method, Voided = false,
        };
        _payments[(clientId, p.Id)] = p;
        return Task.FromResult(p);
    }

    public Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        CreditApplication c = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date, Voided = false,
        };
        _credits[(clientId, c.Id)] = c;
        return Task.FromResult(c);
    }

    public Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default)
    {
        if (_payments.TryGetValue((clientId, documentId), out Payment? p))
            _payments[(clientId, documentId)] = p with { Voided = true };
        return Task.CompletedTask;
    }

    public Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        Task.FromResult(_payments.GetValueOrDefault((clientId, paymentId)));

    public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Payment>>(_payments.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CreditApplication>>(_credits.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default)
    {
        WriteOff w = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date,
            Memo = body.Memo, Voided = false,
        };
        _writeOffs[(clientId, w.Id)] = w;
        return Task.FromResult(w);
    }

    public Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        Task.FromResult(_writeOffs.GetValueOrDefault((clientId, writeOffId)));

    public Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WriteOff>>(_writeOffs.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default)
    {
        if (_writeOffs.TryGetValue((clientId, writeOffId), out WriteOff? w))
            _writeOffs[(clientId, writeOffId)] = w with { Voided = true };
        return Task.CompletedTask;
    }

    public Task<CreditNote> RecordCreditNoteAsync(Guid clientId, CreditNoteBody body, CancellationToken ct = default)
    {
        CreditNote n = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date,
            Memo = body.Memo, Voided = false,
        };
        _creditNotes[(clientId, n.Id)] = n;
        return Task.FromResult(n);
    }

    public Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default) =>
        Task.FromResult(_creditNotes.GetValueOrDefault((clientId, creditNoteId)));

    public Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CreditNote>>(_creditNotes.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default)
    {
        if (_creditNotes.TryGetValue((clientId, creditNoteId), out CreditNote? n))
            _creditNotes[(clientId, creditNoteId)] = n with { Voided = true };
        return Task.CompletedTask;
    }

    public Task<Refund> RecordRefundAsync(Guid clientId, RefundBody body, CancellationToken ct = default)
    {
        Refund r = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date,
            Amount = body.Amount, Voided = false,
        };
        _refunds[(clientId, r.Id)] = r;
        return Task.FromResult(r);
    }

    public Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default) =>
        Task.FromResult(_refunds.GetValueOrDefault((clientId, refundId)));

    public Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Refund>>(_refunds.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task VoidRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default)
    {
        if (_refunds.TryGetValue((clientId, refundId), out Refund? r))
            _refunds[(clientId, refundId)] = r with { Voided = true };
        return Task.CompletedTask;
    }
}
