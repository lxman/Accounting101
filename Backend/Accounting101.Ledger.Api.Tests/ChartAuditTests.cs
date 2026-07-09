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

    [Fact]
    public async Task Multi_dimension_required_set_is_fully_described_and_diffed()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Create directly with TWO required dimensions — the audit line must list both, not just the first.
        Guid createId = Guid.NewGuid();
        string createUrl = $"/clients/{c.ClientId}/accounts/{createId}";
        (await c.Http.PutAsJsonAsync(createUrl, new AccountRequest
        {
            Number = "1200", Name = "Accounts Receivable", Type = "Asset",
            RequiredDimensions = ["Customer", "Invoice"]
        })).EnsureSuccessStatusCode();

        AuditRecordResponse[] createTrail = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit/{createId}"))!;
        Assert.Single(createTrail);
        Assert.Contains("Customer", createTrail[0].Reason);
        Assert.Contains("Invoice", createTrail[0].Reason);   // the second required dimension must not be dropped

        // Adding a second required dimension on an update must produce a diff entry — not silently vanish
        // because the legacy first-or-null accessor didn't change.
        Guid updateId = Guid.NewGuid();
        string updateUrl = $"/clients/{c.ClientId}/accounts/{updateId}";
        (await c.Http.PutAsJsonAsync(updateUrl, new AccountRequest
        {
            Number = "1210", Name = "Retainage Receivable", Type = "Asset",
            RequiredDimensions = ["Customer"]
        })).EnsureSuccessStatusCode();
        (await c.Http.PutAsJsonAsync(updateUrl, new AccountRequest
        {
            Number = "1210", Name = "Retainage Receivable", Type = "Asset",
            RequiredDimensions = ["Customer", "Invoice"]
        })).EnsureSuccessStatusCode();

        AuditRecordResponse[] updateTrail = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit/{updateId}"))!;
        Assert.Equal(2, updateTrail.Length);
        Assert.Equal("AccountUpdated", updateTrail[1].Action);
        Assert.Contains("Invoice", updateTrail[1].Reason);   // the added dimension must be recorded as a change
    }
}
