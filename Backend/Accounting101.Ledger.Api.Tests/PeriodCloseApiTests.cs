using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Period-close gate: a pending (unapproved) entry dated within the period blocks the close and
/// the endpoint surfaces the blockers as a 409 with a machine-readable <c>blockers[]</c> array.
/// </summary>
public sealed class PeriodCloseApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid client, string number, string name, string type)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Close_with_an_in_period_pending_entry_returns_409_with_blockers()
    {
        // Arrange: onboard a client, create accounts, post an entry (no approve) dated 2024-06-30.
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");

        HttpResponseMessage posted = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries",
            new PostEntryRequest(null, new DateOnly(2024, 6, 30), null, null,
            [
                new PostLineRequest(cash, "Debit", 100m),
                new PostLineRequest(revenue, "Credit", 100m),
            ]));
        posted.EnsureSuccessStatusCode();
        // Deliberately NOT approving — entry stays Pending.

        // Act: attempt to close the period through 2024-06-30.
        HttpResponseMessage resp = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/periods/close", new ClosePeriodRequest(new DateOnly(2024, 6, 30)));

        // Assert: 409 Conflict with a blockers array of length 1.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement blockers = body.RootElement.GetProperty("blockers");
        Assert.Equal(1, blockers.GetArrayLength());
        Assert.Equal("2024-06-30", blockers[0].GetProperty("effectiveDate").GetString());
    }
}
