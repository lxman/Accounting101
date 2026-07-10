using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

public sealed class PayrollServiceTests
{
    private static readonly PayrollPostingAccounts Accounts = new()
    {
        SalariesExpenseAccountId    = Guid.NewGuid(),
        PayrollTaxExpenseAccountId  = Guid.NewGuid(),
        CashAccountId               = Guid.NewGuid(),
        WithholdingsPayableAccountId = Guid.NewGuid(),
        PayrollTaxesPayableAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(
        PayrollService Service,
        FakeLedgerClient Ledger,
        InMemoryPayrollRunStore RunStore,
        InMemoryTaxRemittanceStore RemittanceStore);

    private static Harness BuildHarness()
    {
        FakeLedgerClient ledger = new();
        InMemoryPayrollRunStore runStore = new();
        InMemoryTaxRemittanceStore remittanceStore = new();
        PayrollService service = new(runStore, remittanceStore, new FixedPayrollAccountsProvider(Accounts), ledger);
        return new Harness(service, ledger, runStore, remittanceStore);
    }

    // ── RecordRunAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Record_a_run_persists_the_doc_and_posts_a_pending_entry()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        PayrollRunBody body = new(
            Gross: 10_000m, EmployeeFica: 620m, EmployerFica: 620m,
            Deductions: 200m, IncomeTaxWithheld: 1_500m,
            PayDate: new DateOnly(2026, 6, 30), Memo: "June payroll");

        PayrollRun run = await h.Service.RecordRunAsync(clientId, body);

        // Doc persisted and readable
        Assert.NotEqual(Guid.Empty, run.Id);
        Assert.Equal(PayrollRunStatus.Posted, run.Status);

        PayrollRun? fetched = await h.RunStore.GetAsync(clientId, run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(PayrollRunStatus.Posted, fetched!.Status);

        // Ledger received exactly one entry
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(PayrollPosting.PayrollRunSourceType, entry.SourceType);
        Assert.Equal(run.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Record_a_run_entry_has_five_balanced_lines()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        // Gross=10000, EmployeeFica=620, EmployerFica=620, Deductions=200, IncomeTaxWithheld=1500
        // Net pay = 10000 - 620 - 1500 - 200 = 7680
        PayrollRunBody body = new(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null);

        await h.Service.RecordRunAsync(clientId, body);

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(5, entry.Lines.Count);

        // Dr Salaries Expense = Gross
        PostLineRequest salaries = Assert.Single(entry.Lines, l => l.AccountId == Accounts.SalariesExpenseAccountId);
        Assert.Equal("Debit", salaries.Direction);
        Assert.Equal(10_000m, salaries.Amount);

        // Dr Payroll Tax Expense = EmployerFica
        PostLineRequest taxExp = Assert.Single(entry.Lines, l => l.AccountId == Accounts.PayrollTaxExpenseAccountId);
        Assert.Equal("Debit", taxExp.Direction);
        Assert.Equal(620m, taxExp.Amount);

        // Cr Cash = net pay
        PostLineRequest cash = Assert.Single(entry.Lines, l => l.AccountId == Accounts.CashAccountId);
        Assert.Equal("Credit", cash.Direction);
        Assert.Equal(7_680m, cash.Amount);

        // Cr Withholdings Payable = IncomeTaxWithheld + Deductions
        PostLineRequest withholdings = Assert.Single(entry.Lines, l => l.AccountId == Accounts.WithholdingsPayableAccountId);
        Assert.Equal("Credit", withholdings.Direction);
        Assert.Equal(1_700m, withholdings.Amount);

        // Cr Payroll Taxes Payable = EmployeeFica + EmployerFica
        PostLineRequest taxes = Assert.Single(entry.Lines, l => l.AccountId == Accounts.PayrollTaxesPayableAccountId);
        Assert.Equal("Credit", taxes.Direction);
        Assert.Equal(1_240m, taxes.Amount);

        // Balanced: debits = credits
        decimal totalDebits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal totalCredits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(totalDebits, totalCredits);
    }

    // ── VoidRunAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_a_run_withdraws_pending_entry_and_marks_doc_void()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        PayrollRunBody body = new(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null);
        PayrollRun run = await h.Service.RecordRunAsync(clientId, body);

        // Entry is PendingApproval (fake never auto-approves) — so void should call VoidAsync on the client.
        PayrollRun voided = await h.Service.VoidRunAsync(clientId, run.Id, "mistake");

        Assert.Equal(PayrollRunStatus.Void, voided.Status);

        PayrollRun? fetched = await h.RunStore.GetAsync(clientId, run.Id);
        Assert.Equal(PayrollRunStatus.Void, fetched!.Status);
    }

    [Fact]
    public async Task Cannot_void_an_already_voided_run()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        PayrollRunBody body = new(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null);
        PayrollRun run = await h.Service.RecordRunAsync(clientId, body);
        await h.Service.VoidRunAsync(clientId, run.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.VoidRunAsync(clientId, run.Id));
    }

    // ── RecordRemittanceAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Record_a_remittance_persists_the_doc_and_posts_a_pending_entry()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittanceBody body = new(WithholdingsAmount: 1_700m, TaxesAmount: 1_240m,
            PayDate: new DateOnly(2026, 7, 15), Memo: "Q2 taxes");

        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId, body);

        Assert.NotEqual(Guid.Empty, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Posted, remittance.Status);

        TaxRemittance? fetched = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
        Assert.NotNull(fetched);
        Assert.Equal(TaxRemittanceStatus.Posted, fetched!.Status);

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(PayrollPosting.TaxRemittanceSourceType, entry.SourceType);
        Assert.Equal(remittance.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Record_a_remittance_entry_has_three_balanced_lines()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittanceBody body = new(1_700m, 1_240m, new DateOnly(2026, 7, 15), null);

        await h.Service.RecordRemittanceAsync(clientId, body);

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(3, entry.Lines.Count);

        // Dr Withholdings Payable
        PostLineRequest withholdings = Assert.Single(entry.Lines, l => l.AccountId == Accounts.WithholdingsPayableAccountId);
        Assert.Equal("Debit", withholdings.Direction);
        Assert.Equal(1_700m, withholdings.Amount);

        // Dr Payroll Taxes Payable
        PostLineRequest taxes = Assert.Single(entry.Lines, l => l.AccountId == Accounts.PayrollTaxesPayableAccountId);
        Assert.Equal("Debit", taxes.Direction);
        Assert.Equal(1_240m, taxes.Amount);

        // Cr Cash
        PostLineRequest cash = Assert.Single(entry.Lines, l => l.AccountId == Accounts.CashAccountId);
        Assert.Equal("Credit", cash.Direction);
        Assert.Equal(2_940m, cash.Amount);

        decimal totalDebits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal totalCredits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(totalDebits, totalCredits);
    }

    // ── VoidRemittanceAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Void_a_remittance_withdraws_pending_entry_and_marks_doc_void()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittanceBody body = new(1_700m, 1_240m, new DateOnly(2026, 7, 15), null);
        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId, body);

        TaxRemittance voided = await h.Service.VoidRemittanceAsync(clientId, remittance.Id, "error");

        Assert.Equal(TaxRemittanceStatus.Void, voided.Status);

        TaxRemittance? fetched = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Void, fetched!.Status);
    }

    [Fact]
    public async Task Cannot_void_an_already_voided_remittance()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittanceBody body = new(1_700m, 1_240m, new DateOnly(2026, 7, 15), null);
        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId, body);
        await h.Service.VoidRemittanceAsync(clientId, remittance.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.VoidRemittanceAsync(clientId, remittance.Id));
    }

    // ── SourceRef wiring (runs + remittances don't cross-contaminate) ────────

    [Fact]
    public async Task Run_and_remittance_entries_carry_correct_source_refs()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();

        PayrollRun run = await h.Service.RecordRunAsync(clientId,
            new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));
        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));

        Assert.Equal(2, h.Ledger.Posted.Count);
        Assert.Contains(h.Ledger.Posted, e => e.SourceRef == run.Id && e.SourceType == PayrollPosting.PayrollRunSourceType);
        Assert.Contains(h.Ledger.Posted, e => e.SourceRef == remittance.Id && e.SourceType == PayrollPosting.TaxRemittanceSourceType);
    }

    // ── Ledger-truth status overlay (detail reads) ──────────────────────────

    [Fact]
    public async Task GetRun_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        PayrollRun run = await h.Service.RecordRunAsync(clientId,
            new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, run.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        PayrollRun? envelope = await h.RunStore.GetAsync(clientId, run.Id);
        Assert.Equal(PayrollRunStatus.Posted, envelope!.Status);

        PayrollRun? read = await h.Service.GetRunAsync(clientId, run.Id);
        Assert.Equal(PayrollRunStatus.Void, read!.Status);
    }

    [Fact]
    public async Task GetRun_reports_Void_when_ledger_entry_is_reversed_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        PayrollRun run = await h.Service.RecordRunAsync(clientId,
            new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, run.Id)).Id;
        await h.Ledger.ReverseAsync(clientId, entryId, new ReverseRequest(new DateOnly(2026, 7, 1), "reversed directly"));

        PayrollRun? envelope = await h.RunStore.GetAsync(clientId, run.Id);
        Assert.Equal(PayrollRunStatus.Posted, envelope!.Status);

        PayrollRun? read = await h.Service.GetRunAsync(clientId, run.Id);
        Assert.Equal(PayrollRunStatus.Void, read!.Status);
    }

    [Fact]
    public async Task GetRemittance_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, remittance.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        TaxRemittance? envelope = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Posted, envelope!.Status);

        TaxRemittance? read = await h.Service.GetRemittanceAsync(clientId, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Void, read!.Status);
    }

    [Fact]
    public async Task GetRemittance_reports_Void_when_ledger_entry_is_reversed_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, remittance.Id)).Id;
        await h.Ledger.ReverseAsync(clientId, entryId, new ReverseRequest(new DateOnly(2026, 7, 16), "reversed directly"));

        TaxRemittance? envelope = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Posted, envelope!.Status);

        TaxRemittance? read = await h.Service.GetRemittanceAsync(clientId, remittance.Id);
        Assert.Equal(TaxRemittanceStatus.Void, read!.Status);
    }

    // ── Ledger-truth status overlay (list reads, batched) ────────────────────

    [Fact]
    public async Task ListRuns_reports_Void_per_row_from_ledger_truth()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();

        PayrollRun stayPosted = await h.Service.RecordRunAsync(clientId,
            new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));
        PayrollRun goVoid = await h.Service.RecordRunAsync(clientId,
            new PayrollRunBody(20_000m, 1_240m, 1_240m, 0m, 3_000m, new DateOnly(2026, 7, 31), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        PagedResponse<PayrollRun> page = await h.Service.ListRunsAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(PayrollRunStatus.Posted, page.Items.Single(r => r.Id == stayPosted.Id).Status);
        Assert.Equal(PayrollRunStatus.Void, page.Items.Single(r => r.Id == goVoid.Id).Status);
    }

    [Fact]
    public async Task ListRemittances_reports_Void_per_row_from_ledger_truth()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();

        TaxRemittance stayPosted = await h.Service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));
        TaxRemittance goVoid = await h.Service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(900m, 500m, new DateOnly(2026, 8, 15), null));

        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        PagedResponse<TaxRemittance> page = await h.Service.ListRemittancesAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(TaxRemittanceStatus.Posted, page.Items.Single(r => r.Id == stayPosted.Id).Status);
        Assert.Equal(TaxRemittanceStatus.Void, page.Items.Single(r => r.Id == goVoid.Id).Status);
    }
}
