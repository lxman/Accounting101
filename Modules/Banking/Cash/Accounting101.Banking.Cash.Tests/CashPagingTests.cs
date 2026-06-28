using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Focused paging tests for <see cref="ICashDepositStore"/> and <see cref="ICashDisbursementStore"/>.
/// These exercise the in-memory fakes directly to prove: skip/limit pages correctly, Total is the
/// full count regardless of page, and includeVoided widens/narrows both Total and Items.
/// </summary>
public sealed class CashPagingTests
{
    // ── Cash Deposits ───────────────────────────────────────────────────────

    [Fact]
    public async Task Deposits_page1_returns_limit_items_and_correct_total()
    {
        InMemoryCashDepositStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 100m);

        // Record 3 deposits.
        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, i + 1), null, null));

        PagedResponse<CashDeposit> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(0, page1.Skip);
        Assert.Equal(2, page1.Limit);
    }

    [Fact]
    public async Task Deposits_page2_returns_remaining_item()
    {
        InMemoryCashDepositStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 100m);

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, i + 1), $"REF-{i + 1}", null));

        PagedResponse<CashDeposit> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);
        PagedResponse<CashDeposit> page2 = await store.GetByClientPagedAsync(clientId, 2, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page2.Total);
        CashDeposit only = Assert.Single(page2.Items);
        // page 2 item is distinct from page 1 items
        Assert.DoesNotContain(only.Id, page1.Items.Select(d => d.Id));
    }

    [Fact]
    public async Task Deposits_includeVoided_widens_total()
    {
        InMemoryCashDepositStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 100m);

        CashDeposit d1 = await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, 1), null, null));
        await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, 2), null, null));
        await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, 3), null, null));
        await store.VoidAsync(clientId, d1.Id);

        PagedResponse<CashDeposit> withoutVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: false);
        PagedResponse<CashDeposit> withVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(2, withoutVoided.Total);
        Assert.Equal(3, withVoided.Total);
    }

    [Fact]
    public async Task Deposits_descending_order_reverses_sequence()
    {
        InMemoryCashDepositStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 100m);

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, new CashDepositBody([line], new DateOnly(2026, 1, i + 1), null, null));

        PagedResponse<CashDeposit> asc  = await store.GetByClientPagedAsync(clientId, 0, 3, descending: false, includeVoided: false);
        PagedResponse<CashDeposit> desc = await store.GetByClientPagedAsync(clientId, 0, 3, descending: true,  includeVoided: false);

        Assert.Equal(asc.Items[0].Number, desc.Items[2].Number);
        Assert.Equal(asc.Items[2].Number, desc.Items[0].Number);
    }

    // ── Cash Disbursements ──────────────────────────────────────────────────

    [Fact]
    public async Task Disbursements_page1_returns_limit_items_and_correct_total()
    {
        InMemoryCashDisbursementStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 500m);

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, new CashDisbursementBody([line], new DateOnly(2026, 1, i + 1), null, null));

        PagedResponse<CashDisbursement> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
    }

    [Fact]
    public async Task Disbursements_page2_returns_remaining_item()
    {
        InMemoryCashDisbursementStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 500m);

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, new CashDisbursementBody([line], new DateOnly(2026, 1, i + 1), null, null));

        PagedResponse<CashDisbursement> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);
        PagedResponse<CashDisbursement> page2 = await store.GetByClientPagedAsync(clientId, 2, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page2.Total);
        CashDisbursement only = Assert.Single(page2.Items);
        Assert.DoesNotContain(only.Id, page1.Items.Select(d => d.Id));
    }

    [Fact]
    public async Task Disbursements_includeVoided_widens_total()
    {
        InMemoryCashDisbursementStore store = new();
        Guid clientId = Guid.NewGuid();
        CashLine line = new(Guid.NewGuid(), 500m);

        CashDisbursement d1 = await store.RecordAsync(clientId, new CashDisbursementBody([line], new DateOnly(2026, 1, 1), null, null));
        await store.RecordAsync(clientId, new CashDisbursementBody([line], new DateOnly(2026, 1, 2), null, null));
        await store.RecordAsync(clientId, new CashDisbursementBody([line], new DateOnly(2026, 1, 3), null, null));
        await store.VoidAsync(clientId, d1.Id);

        PagedResponse<CashDisbursement> withoutVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: false);
        PagedResponse<CashDisbursement> withVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(2, withoutVoided.Total);
        Assert.Equal(3, withVoided.Total);
    }
}

/// <summary>
/// HTTP-level over-request test: verifies the endpoint echoes the effective clamped limit (200),
/// not the raw requested value (500), when a client sends an over-size limit query parameter.
/// </summary>
public sealed class CashPagingHttpTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    [Fact]
    public async Task Deposits_over_request_limit_envelope_echoes_effective_clamp()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        // No deposits seeded — empty list still exercises the endpoint pagination path.
        PagedResponse<CashDepositView> page = (await clerk.GetFromJsonAsync<PagedResponse<CashDepositView>>(
            $"/clients/{clientId}/cash-deposits?limit=500&skip=0"))!;

        Assert.Equal(200, page.Limit);
        Assert.Equal(0, page.Skip);
    }
}
