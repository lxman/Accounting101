namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// The duplicate lookup behind the import and editor screens' warning: statements already on file for a
/// cash account bearing the same statement date.
/// <para>
/// The Void case is tested here and ONLY here, against the in-memory store. The production store cannot
/// produce a voided statement — <c>GetByAccountAsync</c> queries with the document store's default
/// <c>includeVoided:false</c>, which excludes Voided/Superseded/Inactive, and nothing voids a bank
/// statement in the first place. A Void test driven through the real store would pass whether or not the
/// service filters, which is worth nothing.
/// </para>
/// </summary>
public sealed class BankStatementDuplicateTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid OtherCash = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static (ReconciliationService Svc, InMemoryBankStatementStore Stmts) Build()
    {
        InMemoryBankStatementStore stmts = new();
        ReconciliationService svc = new(stmts, new InMemoryReconciliationStore(), new FakeLedgerReader([], 0m));
        return (svc, stmts);
    }

    private static BankStatementBody Body(Guid cashAccountId, DateOnly date) =>
        new(cashAccountId, date, 0m, 100m, [new BankStatementLine(date, 100m, "dep", null)]);

    [Fact]
    public async Task A_statement_on_the_same_account_and_date_is_returned()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        BankStatement recorded = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal(recorded.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task A_statement_on_another_cash_account_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        await svc.RecordStatementAsync(clientId, Body(OtherCash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task A_statement_on_another_date_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        await svc.RecordStatementAsync(clientId, Body(Cash, new DateOnly(2026, 2, 28)));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    /// <summary>Delete the Status filter in FindDuplicateStatementsAsync and this test fails — that is the
    /// whole point of Seed existing.</summary>
    [Fact]
    public async Task A_voided_statement_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, InMemoryBankStatementStore stmts) = Build();
        stmts.Seed(clientId, Cash, StmtDate, BankStatementStatus.Void);

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task No_matches_returns_an_empty_list()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.NotNull(hits);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Hits_are_ordered_by_number_descending()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        BankStatement first = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));
        BankStatement second = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal([second.Id, first.Id], hits.Select(s => s.Id));
    }

    /// <summary>A cap probe, not a business scenario. GET /bank-statements clamps limit to 200, so a
    /// caller assembling its own scan over that list would silently miss hits past the cap. This lookup
    /// reads the account's statements directly and must return all of them.</summary>
    [Fact]
    public async Task Every_match_is_returned_past_the_list_endpoints_page_cap()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, InMemoryBankStatementStore stmts) = Build();
        for (int i = 0; i < 250; i++) stmts.Seed(clientId, Cash, StmtDate, BankStatementStatus.Posted);

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal(250, hits.Count);
    }
}
