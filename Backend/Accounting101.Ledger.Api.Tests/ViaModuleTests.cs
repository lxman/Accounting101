using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Verifies that AuditStamp.ViaModule is surfaced on the EntryResponse:
/// null for raw clerk entries, and non-null (once wired) for module-originated entries.
/// Task 1: the field exists on the DTO and is null for raw posts (additive baseline).
/// </summary>
public sealed class ViaModuleTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest BalancedEntry(Guid debit, Guid credit, decimal amount) =>
        new(
            Id: null,
            EffectiveDate: new DateOnly(2026, 6, 26),
            Reference: "MOD-TEST",
            Memo: "via-module surfacing test",
            Lines:
            [
                new PostLineRequest(debit, "Debit", amount),
                new PostLineRequest(credit, "Credit", amount),
            ]);

    [Fact]
    public async Task Raw_clerk_post_has_ViaModule_null_on_entry_response()
    {
        SeededClient c = await fixture.SeedClientAsync("ViaModuleTest");

        HttpResponseMessage posted = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries",
            BalancedEntry(Guid.NewGuid(), Guid.NewGuid(), 100m));

        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        EntryResponse? entry = await c.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}");

        Assert.NotNull(entry);
        Assert.Null(entry!.ViaModule);
    }
}
