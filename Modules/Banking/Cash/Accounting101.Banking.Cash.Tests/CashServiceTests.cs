using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Tests;

public sealed class CashServiceTests
{
    private static readonly CashPostingAccounts Accounts = new()
    {
        CashAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(
        CashService Service,
        FakeLedgerClient Ledger,
        InMemoryCashDisbursementStore DisbursementStore,
        InMemoryCashDepositStore DepositStore);

    private static Harness BuildHarness()
    {
        FakeLedgerClient ledger = new();
        InMemoryCashDisbursementStore disbursementStore = new();
        InMemoryCashDepositStore depositStore = new();
        CashService service = new(disbursementStore, depositStore, new FixedCashAccountsProvider(Accounts), ledger);
        return new Harness(service, ledger, disbursementStore, depositStore);
    }

    // ── RecordDisbursementAsync ──────────────────────────────────────────────

    [Fact]
    public async Task Record_a_disbursement_persists_the_doc_and_posts_a_pending_entry()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid interestAccount = Guid.NewGuid();
        Guid loanPayableAccount = Guid.NewGuid();
        CashDisbursementBody body = new(
            Lines: [new CashLine(interestAccount, 500m), new CashLine(loanPayableAccount, 1_500m)],
            Date: new DateOnly(2026, 6, 30),
            Reference: "LOAN-2026-06",
            Memo: "Loan payment — interest + principal");

        CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId, body);

        // Doc persisted and readable
        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.Equal(CashDisbursementStatus.Posted, doc.Status);

        CashDisbursement? fetched = await h.DisbursementStore.GetAsync(clientId, doc.Id);
        Assert.NotNull(fetched);
        Assert.Equal(CashDisbursementStatus.Posted, fetched!.Status);

        // Ledger received exactly one entry
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(CashPosting.DisbursementSourceType, entry.SourceType);
        Assert.Equal(doc.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Record_a_disbursement_entry_has_balanced_lines_Dr_accounts_Cr_cash()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid interestAccount = Guid.NewGuid();
        Guid loanPayableAccount = Guid.NewGuid();
        // loan payment: Dr Interest 500 + Dr Loan Payable 1500 / Cr Cash 2000
        CashDisbursementBody body = new(
            Lines: [new CashLine(interestAccount, 500m), new CashLine(loanPayableAccount, 1_500m)],
            Date: new DateOnly(2026, 6, 30),
            Reference: null,
            Memo: null);

        await h.Service.RecordDisbursementAsync(clientId, body);

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(3, entry.Lines.Count);

        // Dr Interest Expense 500
        PostLineRequest interest = Assert.Single(entry.Lines, l => l.AccountId == interestAccount);
        Assert.Equal("Debit", interest.Direction);
        Assert.Equal(500m, interest.Amount);

        // Dr Loan Payable 1500
        PostLineRequest loan = Assert.Single(entry.Lines, l => l.AccountId == loanPayableAccount);
        Assert.Equal("Debit", loan.Direction);
        Assert.Equal(1_500m, loan.Amount);

        // Cr Cash 2000
        PostLineRequest cash = Assert.Single(entry.Lines, l => l.AccountId == Accounts.CashAccountId);
        Assert.Equal("Credit", cash.Direction);
        Assert.Equal(2_000m, cash.Amount);

        // Balanced
        decimal totalDebits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal totalCredits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(totalDebits, totalCredits);
    }

    // ── VoidDisbursementAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Void_a_disbursement_withdraws_pending_entry_and_marks_doc_void()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        CashDisbursementBody body = new(
            Lines: [new CashLine(expenseAccount, 1_000m)],
            Date: new DateOnly(2026, 6, 15),
            Reference: null,
            Memo: null);
        CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId, body);

        // Entry is PendingApproval (fake never auto-approves) — void should call VoidAsync on the client.
        CashDisbursement voided = await h.Service.VoidDisbursementAsync(clientId, doc.Id, "mistake");

        Assert.Equal(CashDisbursementStatus.Void, voided.Status);

        CashDisbursement? fetched = await h.DisbursementStore.GetAsync(clientId, doc.Id);
        Assert.Equal(CashDisbursementStatus.Void, fetched!.Status);
    }

    [Fact]
    public async Task Cannot_void_an_already_voided_disbursement()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        CashDisbursementBody body = new(
            Lines: [new CashLine(expenseAccount, 1_000m)],
            Date: new DateOnly(2026, 6, 15),
            Reference: null,
            Memo: null);
        CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId, body);
        await h.Service.VoidDisbursementAsync(clientId, doc.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.VoidDisbursementAsync(clientId, doc.Id));
    }

    // ── RecordDepositAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Record_a_deposit_persists_the_doc_and_posts_a_pending_entry()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDepositBody body = new(
            Lines: [new CashLine(capitalAccount, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: "CONTRIB-2026",
            Memo: "Owner contribution");

        CashDeposit doc = await h.Service.RecordDepositAsync(clientId, body);

        // Doc persisted and readable
        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.Equal(CashDepositStatus.Posted, doc.Status);

        CashDeposit? fetched = await h.DepositStore.GetAsync(clientId, doc.Id);
        Assert.NotNull(fetched);
        Assert.Equal(CashDepositStatus.Posted, fetched!.Status);

        // Ledger received exactly one entry
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(CashPosting.DepositSourceType, entry.SourceType);
        Assert.Equal(doc.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Record_a_deposit_entry_has_balanced_lines_Dr_cash_Cr_account()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        // owner contribution: Dr Cash 25000 / Cr Members' Capital 25000
        CashDepositBody body = new(
            Lines: [new CashLine(capitalAccount, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: null,
            Memo: null);

        await h.Service.RecordDepositAsync(clientId, body);

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(2, entry.Lines.Count);

        // Dr Cash 25000
        PostLineRequest cash = Assert.Single(entry.Lines, l => l.AccountId == Accounts.CashAccountId);
        Assert.Equal("Debit", cash.Direction);
        Assert.Equal(25_000m, cash.Amount);

        // Cr Capital 25000
        PostLineRequest capital = Assert.Single(entry.Lines, l => l.AccountId == capitalAccount);
        Assert.Equal("Credit", capital.Direction);
        Assert.Equal(25_000m, capital.Amount);

        // Balanced
        decimal totalDebits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal totalCredits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(totalDebits, totalCredits);
    }

    // ── VoidDepositAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task Void_a_deposit_withdraws_pending_entry_and_marks_doc_void()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDepositBody body = new(
            Lines: [new CashLine(capitalAccount, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: null,
            Memo: null);
        CashDeposit doc = await h.Service.RecordDepositAsync(clientId, body);

        // Entry is PendingApproval (fake never auto-approves) — void should call VoidAsync on the client.
        CashDeposit voided = await h.Service.VoidDepositAsync(clientId, doc.Id, "error");

        Assert.Equal(CashDepositStatus.Void, voided.Status);

        CashDeposit? fetched = await h.DepositStore.GetAsync(clientId, doc.Id);
        Assert.Equal(CashDepositStatus.Void, fetched!.Status);
    }

    [Fact]
    public async Task Cannot_void_an_already_voided_deposit()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDepositBody body = new(
            Lines: [new CashLine(capitalAccount, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: null,
            Memo: null);
        CashDeposit doc = await h.Service.RecordDepositAsync(clientId, body);
        await h.Service.VoidDepositAsync(clientId, doc.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.VoidDepositAsync(clientId, doc.Id));
    }

    // ── SourceRef wiring (disbursements + deposits don't cross-contaminate) ──

    [Fact]
    public async Task Disbursement_and_deposit_entries_carry_correct_source_refs()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();

        CashDisbursement disbursement = await h.Service.RecordDisbursementAsync(clientId,
            new CashDisbursementBody([new CashLine(expenseAccount, 1_000m)], new DateOnly(2026, 6, 15), null, null));
        CashDeposit deposit = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 25_000m)], new DateOnly(2026, 1, 2), null, null));

        Assert.Equal(2, h.Ledger.Posted.Count);
        Assert.Contains(h.Ledger.Posted, e => e.SourceRef == disbursement.Id && e.SourceType == CashPosting.DisbursementSourceType);
        Assert.Contains(h.Ledger.Posted, e => e.SourceRef == deposit.Id && e.SourceType == CashPosting.DepositSourceType);
    }

    // ── Ledger-truth status overlay (detail reads) ──────────────────────────

    [Fact]
    public async Task GetDeposit_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDeposit doc = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 25_000m)], new DateOnly(2026, 1, 2), null, null));

        // Simulate the crash: the GL entry is withdrawn, but the document envelope was never marked void.
        IReadOnlyList<EntryResponse> spawned = await h.Ledger.GetEntriesBySourceRefAsync(clientId, doc.Id);
        Guid entryId = Assert.Single(spawned).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        // Envelope still says Posted…
        CashDeposit? envelope = await h.DepositStore.GetAsync(clientId, doc.Id);
        Assert.Equal(CashDepositStatus.Posted, envelope!.Status);

        // …but the service read reports ledger-truth Void.
        CashDeposit? read = await h.Service.GetDepositAsync(clientId, doc.Id);
        Assert.Equal(CashDepositStatus.Void, read!.Status);
    }

    [Fact]
    public async Task GetDeposit_reports_Void_when_ledger_entry_is_reversed_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDeposit doc = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 25_000m)], new DateOnly(2026, 1, 2), null, null));

        // Simulate reversed-after-posting: a reversal entry is created directly (original stays Active),
        // and the document envelope is never marked void.
        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, doc.Id)).Id;
        await h.Ledger.ReverseAsync(clientId, entryId, new ReverseRequest(new DateOnly(2026, 1, 3), "reversed directly"));

        CashDeposit? envelope = await h.DepositStore.GetAsync(clientId, doc.Id);
        Assert.Equal(CashDepositStatus.Posted, envelope!.Status);   // envelope still Posted

        CashDeposit? read = await h.Service.GetDepositAsync(clientId, doc.Id);
        Assert.Equal(CashDepositStatus.Void, read!.Status);          // ledger-truth via the reversal
    }

    [Fact]
    public async Task GetDisbursement_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId,
            new CashDisbursementBody([new CashLine(expenseAccount, 1_000m)], new DateOnly(2026, 6, 15), null, null));

        IReadOnlyList<EntryResponse> spawned = await h.Ledger.GetEntriesBySourceRefAsync(clientId, doc.Id);
        Guid entryId = Assert.Single(spawned).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        CashDisbursement? envelope = await h.DisbursementStore.GetAsync(clientId, doc.Id);
        Assert.Equal(CashDisbursementStatus.Posted, envelope!.Status);

        CashDisbursement? read = await h.Service.GetDisbursementAsync(clientId, doc.Id);
        Assert.Equal(CashDisbursementStatus.Void, read!.Status);
    }

    // ── Ledger-truth status overlay (list reads, batched) ────────────────────

    [Fact]
    public async Task ListDeposits_reports_Void_per_row_from_ledger_truth()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();

        CashDeposit stayPosted = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 10_000m)], new DateOnly(2026, 1, 1), null, null));
        CashDeposit goVoid = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 20_000m)], new DateOnly(2026, 1, 2), null, null));

        // Withdraw the second deposit's entry directly — envelope stays Posted.
        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        PagedResponse<CashDeposit> page = await h.Service.ListDepositsAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(CashDepositStatus.Posted, page.Items.Single(d => d.Id == stayPosted.Id).Status);
        Assert.Equal(CashDepositStatus.Void, page.Items.Single(d => d.Id == goVoid.Id).Status);
    }

    [Fact]
    public async Task ListDisbursements_reports_Void_per_row_from_ledger_truth()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();

        CashDisbursement stayPosted = await h.Service.RecordDisbursementAsync(clientId,
            new CashDisbursementBody([new CashLine(expenseAccount, 1_000m)], new DateOnly(2026, 6, 1), null, null));
        CashDisbursement goVoid = await h.Service.RecordDisbursementAsync(clientId,
            new CashDisbursementBody([new CashLine(expenseAccount, 2_000m)], new DateOnly(2026, 6, 2), null, null));

        // Withdraw the second disbursement's entry directly — envelope stays Posted.
        Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
        await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

        PagedResponse<CashDisbursement> page = await h.Service.ListDisbursementsAsync(clientId, 0, 50, descending: false, includeVoided: true);

        Assert.Equal(CashDisbursementStatus.Posted, page.Items.Single(d => d.Id == stayPosted.Id).Status);
        Assert.Equal(CashDisbursementStatus.Void, page.Items.Single(d => d.Id == goVoid.Id).Status);
    }

    // ── Batch-read discipline: a list folds in ONE batch call; a detail passes through one singular call ──

    [Fact]
    public async Task ListDeposits_folds_ledger_truth_in_one_batch_call_never_per_row()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
            await h.Service.RecordDepositAsync(clientId,
                new CashDepositBody([new CashLine(capitalAccount, 1_000m * (i + 1))], new DateOnly(2026, 1, i + 1), null, null));

        await h.Service.ListDepositsAsync(clientId, 0, 50, descending: false, includeVoided: true);

        // ONE batched ledger read for the whole page — never a per-row singular fan-out (N+1).
        IReadOnlyList<Guid> batch = Assert.Single(h.Ledger.BatchCalls);
        Assert.Equal(3, batch.Count);
        Assert.Empty(h.Ledger.SingularCalls);
    }

    [Fact]
    public async Task ListDisbursements_folds_ledger_truth_in_one_batch_call_never_per_row()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
            await h.Service.RecordDisbursementAsync(clientId,
                new CashDisbursementBody([new CashLine(expenseAccount, 1_000m * (i + 1))], new DateOnly(2026, 6, i + 1), null, null));

        await h.Service.ListDisbursementsAsync(clientId, 0, 50, descending: false, includeVoided: true);

        IReadOnlyList<Guid> batch = Assert.Single(h.Ledger.BatchCalls);
        Assert.Equal(3, batch.Count);
        Assert.Empty(h.Ledger.SingularCalls);
    }

    [Fact]
    public async Task GetDeposit_passes_through_one_singular_read_never_the_batch()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid capitalAccount = Guid.NewGuid();
        CashDeposit doc = await h.Service.RecordDepositAsync(clientId,
            new CashDepositBody([new CashLine(capitalAccount, 25_000m)], new DateOnly(2026, 1, 2), null, null));

        await h.Service.GetDepositAsync(clientId, doc.Id);

        Assert.Equal(doc.Id, Assert.Single(h.Ledger.SingularCalls));
        Assert.Empty(h.Ledger.BatchCalls);
    }

    [Fact]
    public async Task GetDisbursement_passes_through_one_singular_read_never_the_batch()
    {
        Harness h = BuildHarness();
        Guid clientId = Guid.NewGuid();
        Guid expenseAccount = Guid.NewGuid();
        CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId,
            new CashDisbursementBody([new CashLine(expenseAccount, 1_000m)], new DateOnly(2026, 6, 15), null, null));

        await h.Service.GetDisbursementAsync(clientId, doc.Id);

        Assert.Equal(doc.Id, Assert.Single(h.Ledger.SingularCalls));
        Assert.Empty(h.Ledger.BatchCalls);
    }
}
