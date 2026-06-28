using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Reopen's behavioral effect (beyond the authz checks in ReopenTests): a full reopen re-opens a
/// frozen period so a previously-blocked backdated post succeeds; a partial reopen moves the freeze to an
/// earlier date (posts after the new freeze succeed, posts still before it are rejected); and a reopen that
/// does not move earlier than the current close is refused.</summary>
public sealed class ReopenEffectE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(SeededClient c, Guid cash, Guid revenue)> ArrangeClosedAsync(DateOnly closeThrough)
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");
        await PostAndApproveAsync(c.Http, c.ClientId, closeThrough, cash, revenue, 100m);
        (await CloseAsync(c.Http, c.ClientId, closeThrough)).EnsureSuccessStatusCode();
        return (c, cash, revenue);
    }

    [Fact]
    public async Task A_full_reopen_lets_a_previously_blocked_backdated_post_succeed()
    {
        (SeededClient c, Guid cash, Guid revenue) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Frozen: a backdated post is refused.
        HttpResponseMessage blocked = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        await AssertProblemAsync(blocked, HttpStatusCode.Conflict, "closed");

        // Full reopen (clear the freeze).
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReopenAsync(fixture, c.ClientId, c.UserId, null, "closed too early")).StatusCode);

        // The same backdated post now succeeds.
        HttpResponseMessage ok = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task A_partial_reopen_moves_the_freeze_earlier()
    {
        (SeededClient c, Guid cash, Guid revenue) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Reopen the freeze back to 2026-02-28 (closed-through now 2026-02-28).
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReopenAsync(fixture, c.ClientId, c.UserId, new DateOnly(2026, 2, 28), "partial")).StatusCode);

        // A post dated AFTER the new freeze (in March, previously closed) now succeeds.
        HttpResponseMessage march = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, march.StatusCode);

        // A post still on/before the new freeze (2026-02-28) is still rejected.
        HttpResponseMessage feb = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 2, 28), cash, revenue, 10m);
        await AssertProblemAsync(feb, HttpStatusCode.Conflict, "closed");
    }

    [Fact]
    public async Task A_reopen_that_does_not_move_earlier_is_refused()
    {
        (SeededClient c, _, _) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Reopen "through" a date >= the current close is not a reopen — refused.
        HttpResponseMessage resp = await ReopenAsync(fixture, c.ClientId, c.UserId, new DateOnly(2026, 4, 30), "noop");
        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "earlier");
    }
}
