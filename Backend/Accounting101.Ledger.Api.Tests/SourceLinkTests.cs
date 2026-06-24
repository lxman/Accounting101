using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The source-document back-link end to end: an upstream module stamps an entry with the document that
/// produced it (SourceRef) and a discriminator for which module owns that document (SourceType); both
/// round-trip on the entry, and the module can resolve its document back to the journal by SourceRef.
/// </summary>
public sealed class SourceLinkTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task An_entry_carries_its_source_back_link_and_is_resolvable_by_it()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid invoice = Guid.NewGuid();

        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), Reference: "INV-1042", Memo: null,
            Lines: [new PostLineRequest(ar, "Debit", 100m), new PostLineRequest(revenue, "Credit", 100m)],
            SourceRef: invoice, SourceType: "Invoice");

        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        // The back-link round-trips on the entry itself.
        EntryResponse read = (await c.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}"))!;
        Assert.Equal(invoice, read.SourceRef);
        Assert.Equal("Invoice", read.SourceType);

        // The module resolves its document back to the journal without knowing the entry id.
        List<EntryResponse> fromInvoice = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRef={invoice}"))!;
        Assert.Equal(created.Id, Assert.Single(fromInvoice).Id);
    }

    [Fact]
    public async Task A_reversal_inherits_the_source_link_so_the_document_resolves_both_entries()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid invoice = Guid.NewGuid();

        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", 100m), new PostLineRequest(revenue, "Credit", 100m)],
            SourceRef: invoice, SourceType: "Invoice");

        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();

        // Reverse it; the reversal carries no source ref of its own — it must inherit the original's.
        EntryResponse reversal = (await (await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{created.Id}/reverse",
            new ReverseRequest(new DateOnly(2026, 4, 1), "voided invoice")))
            .Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(invoice, reversal.SourceRef);
        Assert.Equal("Invoice", reversal.SourceType);

        // The document now resolves to both the original entry and its reversal.
        List<EntryResponse> fromInvoice = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRef={invoice}"))!;
        Assert.Equal(2, fromInvoice.Count);
        Assert.Contains(fromInvoice, e => e.Id == created.Id);
        Assert.Contains(fromInvoice, e => e.Id == reversal.Id);
    }

    [Fact]
    public async Task Reference_and_Memo_round_trip_on_entry_read_back()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();

        PostEntryRequest entry = new(
            null, new DateOnly(2026, 6, 30), Reference: "CHK-4201", Memo: "June rent payment",
            Lines: [new PostLineRequest(debit, "Debit", 500m), new PostLineRequest(credit, "Credit", 500m)]);

        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        EntryResponse read = (await c.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}"))!;
        Assert.Equal("CHK-4201", read.Reference);
        Assert.Equal("June rent payment", read.Memo);
    }

    [Fact]
    public async Task An_entry_with_no_source_document_reports_a_null_back_link()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cash, "Debit", 75m), new PostLineRequest(revenue, "Credit", 75m)]);

        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        EntryResponse read = (await c.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}"))!;
        Assert.Null(read.SourceRef);
        Assert.Null(read.SourceType);
    }
}
