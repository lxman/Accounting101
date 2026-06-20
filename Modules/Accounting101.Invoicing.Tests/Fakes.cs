using System.Collections.Concurrent;
using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

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
    private readonly ConcurrentDictionary<(Guid, Guid), Invoice> _store = new();

    public Task SaveAsync(Guid clientId, Invoice invoice, CancellationToken cancellationToken = default)
    {
        _store[(clientId, invoice.Id)] = invoice;
        return Task.CompletedTask;
    }

    public Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, invoiceId)));
}

internal sealed class FakeInvoiceNumbers : IInvoiceNumbers
{
    private int _next;
    public Task<string> NextAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult($"INV-{Interlocked.Increment(ref _next):D4}");
}

internal sealed class FixedAccountsProvider(InvoicePostingAccounts accounts) : IInvoiceAccountsProvider
{
    public Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accounts);
}
