using System.Collections.Concurrent;
using Accounting101.Receivables;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
/// </summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>The most recently posted entry, or null if nothing has posted yet.</summary>
    public PostEntryRequest? LastPosted { get; private set; }

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), Payment> _payments = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), CreditApplication> _credits = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), WriteOff> _writeOffs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), CreditNote> _creditNotes = new();

    public Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        Payment p = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date, Amount = body.Amount,
            Method = body.Method, Allocations = body.Allocations, Voided = false,
        };
        _payments[(clientId, p.Id)] = p;
        return Task.FromResult(p);
    }

    public Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        CreditApplication c = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date,
            Allocations = body.Allocations, Voided = false,
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
            Allocations = body.Allocations, Voided = false,
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
            Allocations = body.Allocations, Voided = false,
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
}
