using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

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
