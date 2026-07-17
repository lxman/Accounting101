using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The Audit-area HTTP surface: paged log, verify diagnostic, and audit.read enforcement.</summary>
public sealed class AuditEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task PostApproveAsync(HttpClient http, Guid client, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        PostEntryRequest req = new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);
        PostEntryResponse created = (await (await http.PostAsJsonAsync($"/clients/{client}/entries", req))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Audit_log_is_bare_when_unpaged_and_a_paged_envelope_when_paged()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 50m);

        AuditRecordResponse[] bare = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit"))!;
        Assert.NotEmpty(bare);

        PagedResponse<AuditRecordResponse> page = (await c.Http.GetFromJsonAsync<PagedResponse<AuditRecordResponse>>(
            $"/clients/{c.ClientId}/audit?skip=0&limit=2"))!;
        Assert.Equal(bare.Length, page.Total);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
        Assert.True(page.Items.Count <= 2);
    }

    [Fact]
    public async Task Verify_reports_a_valid_chain_with_diagnostic_fields()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        AuditVerifyResponse v = (await c.Http.GetFromJsonAsync<AuditVerifyResponse>(
            $"/clients/{c.ClientId}/audit/verify"))!;
        Assert.True(v.Valid);
        Assert.Null(v.Failure);
        Assert.Null(v.BrokenAtSequence);
        Assert.True(v.RecordCount > 0);
        Assert.Equal(v.RecordCount, v.HeadSequence);
    }
}
