using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves the Inventory account's control-account enforcement: once configured
/// RequiredDimensions = ["Item"], the engine rejects any posted line on that account lacking the
/// {Item} tag with 422. Enforcement only — Task 2 already dimensions every posted Inventory line, so
/// this is the engine-side backstop, not a behavior change to the module.</summary>
public sealed class InventoryLedgerFirstProofTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name,
        string type, IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Inventory_account_rejects_an_untagged_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await PutAccountAsync(http, clientId, fixture.InventoryAssetAccountId, "1400", "Inventory", "Asset", ["Item"]);
        await PutAccountAsync(http, clientId, fixture.GrniClearingAccountId, "2050", "GRNI", "Liability");

        PostEntryRequest untagged = new(null, new DateOnly(2026, 6, 30), "R", "m",
        [
            new PostLineRequest(fixture.InventoryAssetAccountId, "Debit", 100m),   // no {Item}
            new PostLineRequest(fixture.GrniClearingAccountId, "Credit", 100m),
        ]);

        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/entries", untagged);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
