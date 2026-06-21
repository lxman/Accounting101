using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Chart-of-accounts changes are control-relevant, so they land on the client's tamper-evident audit
/// chain: create, rename, and deactivate each record an attributed event with a before/after summary,
/// queryable by the account, and the chain (now carrying account changes alongside ledger events) still
/// verifies.
/// </summary>
public sealed class ChartAuditTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Account_create_rename_and_deactivate_are_recorded_and_attributed()
    {
        SeededClient c = await fixture.SeedClientAsync(); // Controller — may manage accounts
        Guid id = Guid.NewGuid();
        string url = $"/clients/{c.ClientId}/accounts/{id}";

        (await c.Http.PutAsJsonAsync(url, new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await c.Http.PutAsJsonAsync(url, new AccountRequest { Number = "1000", Name = "Petty Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await c.Http.PutAsJsonAsync(url, new AccountRequest { Number = "1000", Name = "Petty Cash", Type = "Asset", Active = false }))
            .EnsureSuccessStatusCode();

        // The account's own audit trail (records are keyed by the account id).
        AuditRecordResponse[] trail = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit/{id}"))!;

        Assert.Equal(3, trail.Length);
        Assert.Equal("AccountCreated", trail[0].Action);
        Assert.Equal(c.UserId, trail[0].Actor.UserId);                 // attributed to who did it

        Assert.Equal("AccountUpdated", trail[1].Action);
        Assert.Contains("Name", trail[1].Reason);                      // the rename's before/after is recorded

        Assert.Equal("AccountUpdated", trail[2].Action);
        Assert.Contains("Active", trail[2].Reason);                    // the deactivation is recorded

        // The chain now carries chart changes too, and still verifies.
        AuditVerifyResponse verify = (await c.Http.GetFromJsonAsync<AuditVerifyResponse>(
            $"/clients/{c.ClientId}/audit/verify"))!;
        Assert.True(verify.Valid);
    }
}
