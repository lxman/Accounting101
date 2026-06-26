using System.Net;
using System.Net.Http.Json;
using System.Text;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Strict JSON binding: unmapped fields on POST /entries must be rejected with 400, naming the
/// offending field, so that callers get immediate feedback rather than silent data loss.
/// </summary>
public sealed class StrictBindingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Unknown_field_in_entry_post_returns_400_naming_the_field()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // A balanced entry body PLUS an unmapped field "bogus".
        string json = """
            {
                "effectiveDate": "2026-03-31",
                "lines": [
                    { "accountId": "00000000-0000-0000-0000-000000000001", "direction": "Debit",  "amount": 100 },
                    { "accountId": "00000000-0000-0000-0000-000000000002", "direction": "Credit", "amount": 100 }
                ],
                "bogus": 1
            }
            """;

        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await c.Http.PostAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("bogus", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Misnamed_date_field_is_rejected_not_silently_defaulted()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // "date" is not a mapped field — should be "effectiveDate".
        string json = """
            {
                "date": "2026-03-31",
                "lines": [
                    { "accountId": "00000000-0000-0000-0000-000000000001", "direction": "Debit",  "amount": 100 },
                    { "accountId": "00000000-0000-0000-0000-000000000002", "direction": "Credit", "amount": 100 }
                ]
            }
            """;

        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await c.Http.PostAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("date", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Well_formed_entry_post_still_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // No chart set up → unrestricted posting (see PostingValidationTests). Only mapped fields.
        PostEntryRequest req = new(
            Id: null,
            EffectiveDate: new DateOnly(2026, 3, 31),
            Reference: null,
            Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit",  100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ]);

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", req);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
