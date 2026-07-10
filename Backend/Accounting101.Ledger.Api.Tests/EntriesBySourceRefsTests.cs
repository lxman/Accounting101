using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The batch source-back-link read: a caller resolves the journal entries for several source
/// documents in one request via the CSV <c>sourceRefs</c> filter. Malformed input is a 400; an empty
/// list is an empty bare array; the branch is a peer of the singular <c>sourceRef</c>.
/// </summary>
public sealed class EntriesBySourceRefsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> PostWithSource(SeededClient c, Guid sourceRef)
    {
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(debit, "Debit", 100m), new PostLineRequest(credit, "Credit", 100m)],
            SourceRef: sourceRef, SourceType: "Invoice");
        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        return created.Id;
    }

    [Fact]
    public async Task Batch_sourceRefs_returns_the_union_across_documents()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid docA = Guid.NewGuid(), docB = Guid.NewGuid(), docUnrelated = Guid.NewGuid();
        Guid entryA = await PostWithSource(c, docA);
        Guid entryB = await PostWithSource(c, docB);
        await PostWithSource(c, docUnrelated);

        List<EntryResponse> union = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRefs={docA},{docB}"))!;

        Assert.Equal(2, union.Count);
        Assert.Contains(union, e => e.Id == entryA);
        Assert.Contains(union, e => e.Id == entryB);
    }

    [Fact]
    public async Task Malformed_sourceRefs_is_a_400()
    {
        SeededClient c = await fixture.SeedClientAsync();
        HttpResponseMessage response = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/entries?sourceRefs={Guid.NewGuid()},not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Empty_sourceRefs_returns_empty_array()
    {
        SeededClient c = await fixture.SeedClientAsync();
        List<EntryResponse> union = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRefs="))!;
        Assert.Empty(union);
    }

    [Fact]
    public async Task SourceRefs_trims_whitespace_around_elements()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid docA = Guid.NewGuid(), docB = Guid.NewGuid();
        Guid entryA = await PostWithSource(c, docA);
        Guid entryB = await PostWithSource(c, docB);

        // Spaces around and between the comma-separated Guids must be trimmed, not rejected.
        List<EntryResponse> union = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRefs= {docA} , {docB} "))!;

        Assert.Equal(2, union.Count);
        Assert.Contains(union, e => e.Id == entryA);
        Assert.Contains(union, e => e.Id == entryB);
    }

    [Fact]
    public async Task Singular_sourceRef_takes_precedence_when_both_are_supplied()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid docA = Guid.NewGuid(), docB = Guid.NewGuid();
        Guid entryA = await PostWithSource(c, docA);
        await PostWithSource(c, docB);

        // Both supplied: singular sourceRef wins; only docA's entry comes back.
        List<EntryResponse> result = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRef={docA}&sourceRefs={docB}"))!;

        Assert.Equal(entryA, Assert.Single(result).Id);
    }
}
