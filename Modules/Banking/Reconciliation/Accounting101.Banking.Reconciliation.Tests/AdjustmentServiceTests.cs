namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AdjustmentServiceTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Offset = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static (AdjustmentService svc, InMemoryReconciliationStore recs, InMemoryBankAdjustmentStore adjs, FakePostingLedger ledger) Build()
    {
        InMemoryReconciliationStore recs = new();
        InMemoryBankAdjustmentStore adjs = new();
        FakePostingLedger ledger = new();
        return (new AdjustmentService(recs, adjs, ledger), recs, adjs, ledger);
    }

    [Fact]
    public async Task Recording_a_charge_posts_a_pending_entry_dr_offset_cr_cash_and_stores_a_doc()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, InMemoryBankAdjustmentStore adjs, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);

        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id,
            new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, "bank fee"));

        Assert.NotNull(ledger.Posted);
        Assert.Contains(ledger.Posted!.Lines, l => l.AccountId == Offset && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(ledger.Posted!.Lines, l => l.AccountId == Cash && l.Direction == "Credit" && l.Amount == 5m);
        Assert.Equal(StmtDate, ledger.Posted!.EffectiveDate);       // defaulted to the statement date
        Assert.NotNull(await adjs.GetAsync(clientId, adj.Id));        // doc stored
        Assert.Equal(BankAdjustmentStatus.Posted, adj.Status);
    }

    [Fact]
    public async Task Recording_against_a_completed_reconciliation_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await recs.SaveAsync(clientId, rec with { Status = ReconciliationStatus.Completed });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null)));
    }

    [Fact]
    public async Task A_non_positive_amount_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 0m, AdjustmentKind.Charge, null, null)));
    }

    [Fact]
    public async Task Voiding_a_pending_adjustment_withdraws_the_entry_and_marks_the_doc_void()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        ledger.EntryPosting = "PendingApproval";
        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));

        BankAdjustment voided = await svc.VoidAdjustmentAsync(clientId, adj.Id);

        Assert.True(ledger.Voided);
        Assert.False(ledger.Reversed);
        Assert.Equal(BankAdjustmentStatus.Void, voided.Status);
    }

    [Fact]
    public async Task Voiding_an_approved_adjustment_reverses_the_entry()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        ledger.EntryPosting = "Posted";   // simulate the entry already approved
        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));

        await svc.VoidAdjustmentAsync(clientId, adj.Id);

        Assert.True(ledger.Reversed);
        Assert.False(ledger.Voided);
    }

    [Fact]
    public async Task List_returns_the_reconciliations_adjustments()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));
        await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 3m, AdjustmentKind.Credit, null, null));

        IReadOnlyList<BankAdjustment> list = await svc.ListAdjustmentsAsync(clientId, rec.Id);
        Assert.Equal(2, list.Count);
    }
}
