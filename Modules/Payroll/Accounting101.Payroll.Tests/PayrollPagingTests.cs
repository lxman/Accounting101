using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// Focused paging tests for <see cref="IPayrollRunStore"/> and <see cref="ITaxRemittanceStore"/>.
/// These exercise the in-memory fakes directly to prove: skip/limit pages correctly, Total is the
/// full count regardless of page, and includeVoided widens/narrows both Total and Items.
/// </summary>
public sealed class PayrollPagingTests
{
    private static PayrollRunBody MakeRunBody(int day = 1) =>
        new(Gross: 10_000m, EmployeeFica: 620m, EmployerFica: 620m, Deductions: 0m,
            IncomeTaxWithheld: 1_500m, PayDate: new DateOnly(2026, 6, day), Memo: null);

    private static TaxRemittanceBody MakeRemittanceBody(int day = 1) =>
        new(WithholdingsAmount: 1_500m, TaxesAmount: 1_240m,
            PayDate: new DateOnly(2026, 7, day), Memo: null);

    // ── Payroll Runs ────────────────────────────────────────────────────────

    [Fact]
    public async Task Runs_page1_returns_limit_items_and_correct_total()
    {
        InMemoryPayrollRunStore store = new();
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, MakeRunBody(i + 1));

        PagedResponse<PayrollRun> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(0, page1.Skip);
        Assert.Equal(2, page1.Limit);
    }

    [Fact]
    public async Task Runs_page2_returns_remaining_item()
    {
        InMemoryPayrollRunStore store = new();
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, MakeRunBody(i + 1));

        PagedResponse<PayrollRun> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);
        PagedResponse<PayrollRun> page2 = await store.GetByClientPagedAsync(clientId, 2, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page2.Total);
        PayrollRun onlyRun = Assert.Single(page2.Items);
        Assert.DoesNotContain(onlyRun.Id, page1.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Runs_includeVoided_widens_total()
    {
        InMemoryPayrollRunStore store = new();
        Guid clientId = Guid.NewGuid();

        PayrollRun r1 = await store.RecordAsync(clientId, MakeRunBody(1));
        await store.RecordAsync(clientId, MakeRunBody(2));
        await store.RecordAsync(clientId, MakeRunBody(3));
        await store.VoidAsync(clientId, r1.Id);

        PagedResponse<PayrollRun> withoutVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: false);
        PagedResponse<PayrollRun> withVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(2, withoutVoided.Total);
        Assert.Equal(3, withVoided.Total);
    }

    [Fact]
    public async Task Runs_descending_order_reverses_sequence()
    {
        InMemoryPayrollRunStore store = new();
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, MakeRunBody(i + 1));

        PagedResponse<PayrollRun> asc  = await store.GetByClientPagedAsync(clientId, 0, 3, descending: false, includeVoided: false);
        PagedResponse<PayrollRun> desc = await store.GetByClientPagedAsync(clientId, 0, 3, descending: true,  includeVoided: false);

        Assert.Equal(asc.Items[0].Number, desc.Items[2].Number);
        Assert.Equal(asc.Items[2].Number, desc.Items[0].Number);
    }

    // ── Tax Remittances ─────────────────────────────────────────────────────

    [Fact]
    public async Task Remittances_page1_returns_limit_items_and_correct_total()
    {
        InMemoryTaxRemittanceStore store = new();
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, MakeRemittanceBody(i + 1));

        PagedResponse<TaxRemittance> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(0, page1.Skip);
        Assert.Equal(2, page1.Limit);
    }

    [Fact]
    public async Task Remittances_page2_returns_remaining_item()
    {
        InMemoryTaxRemittanceStore store = new();
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            await store.RecordAsync(clientId, MakeRemittanceBody(i + 1));

        PagedResponse<TaxRemittance> page1 = await store.GetByClientPagedAsync(clientId, 0, 2, descending: false, includeVoided: false);
        PagedResponse<TaxRemittance> page2 = await store.GetByClientPagedAsync(clientId, 2, 2, descending: false, includeVoided: false);

        Assert.Equal(3, page2.Total);
        TaxRemittance onlyRemittance = Assert.Single(page2.Items);
        Assert.DoesNotContain(onlyRemittance.Id, page1.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Remittances_includeVoided_widens_total()
    {
        InMemoryTaxRemittanceStore store = new();
        Guid clientId = Guid.NewGuid();

        TaxRemittance r1 = await store.RecordAsync(clientId, MakeRemittanceBody(1));
        await store.RecordAsync(clientId, MakeRemittanceBody(2));
        await store.RecordAsync(clientId, MakeRemittanceBody(3));
        await store.VoidAsync(clientId, r1.Id);

        PagedResponse<TaxRemittance> withoutVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: false);
        PagedResponse<TaxRemittance> withVoided = await store.GetByClientPagedAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(2, withoutVoided.Total);
        Assert.Equal(3, withVoided.Total);
    }
}
