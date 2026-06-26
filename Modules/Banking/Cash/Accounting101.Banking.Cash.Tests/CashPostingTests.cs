using Accounting101.Banking.Cash;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Tests;

public sealed class CashPostingTests
{
    // Helper: signed amount — debits positive, credits negative — for balance checks.
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static CashPostingAccounts MakeAccounts(out Guid cashAccount)
    {
        cashAccount = Guid.NewGuid();
        return new CashPostingAccounts { CashAccountId = cashAccount };
    }

    // Test 1: disbursement with two lines — Dr A 500, Dr B 1500, Cr Cash 2000; balanced.
    [Fact]
    public void ComposeDisbursement_two_lines_produces_correct_debits_and_credit_balanced()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out Guid cash);
        Guid accountA = Guid.NewGuid();
        Guid accountB = Guid.NewGuid();

        var body = new CashDisbursementBody(
            Lines: [new CashLine(accountA, 500m), new CashLine(accountB, 1500m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: "REF-001",
            Memo: "Loan payment");

        PostEntryRequest entry = CashPosting.ComposeDisbursement(id, body, accounts);

        // Dr each line account
        PostLineRequest lineA = entry.Lines.Single(l => l.AccountId == accountA);
        Assert.Equal("Debit", lineA.Direction);
        Assert.Equal(500m, lineA.Amount);

        PostLineRequest lineB = entry.Lines.Single(l => l.AccountId == accountB);
        Assert.Equal("Debit", lineB.Direction);
        Assert.Equal(1500m, lineB.Amount);

        // Cr Cash = total
        PostLineRequest cashLine = entry.Lines.Single(l => l.AccountId == cash);
        Assert.Equal("Credit", cashLine.Direction);
        Assert.Equal(2000m, cashLine.Amount);

        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
    }

    // Test 2: lines sharing an account collapse to one debit; lines ordered deterministically by account id.
    [Fact]
    public void ComposeDisbursement_lines_sharing_account_collapse_and_order_by_account_id()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        // Use controlled GUIDs so we know the expected order
        Guid acctLow = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid acctHigh = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        var body = new CashDisbursementBody(
            Lines:
            [
                new CashLine(acctHigh, 300m),
                new CashLine(acctLow, 200m),
                new CashLine(acctLow, 100m),  // same account as above — should collapse
            ],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        PostEntryRequest entry = CashPosting.ComposeDisbursement(id, body, accounts);

        // acctLow collapsed: 200+100=300
        PostLineRequest lowLine = entry.Lines.Single(l => l.AccountId == acctLow);
        Assert.Equal("Debit", lowLine.Direction);
        Assert.Equal(300m, lowLine.Amount);

        PostLineRequest highLine = entry.Lines.Single(l => l.AccountId == acctHigh);
        Assert.Equal("Debit", highLine.Direction);
        Assert.Equal(300m, highLine.Amount);

        // Debit lines come before the Cash credit and are ordered by account id (low before high)
        IList<PostLineRequest> debitLines = entry.Lines.Where(l => l.Direction == "Debit").ToList();
        Assert.Equal(2, debitLines.Count);
        Assert.Equal(acctLow,  debitLines[0].AccountId);
        Assert.Equal(acctHigh, debitLines[1].AccountId);
    }

    // Test 3: deposit — Dr Cash 25000, Cr MembersCapital 25000; balanced.
    [Fact]
    public void ComposeDeposit_single_line_produces_correct_debit_and_credit_balanced()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out Guid cash);
        Guid membersCapital = Guid.NewGuid();

        var body = new CashDepositBody(
            Lines: [new CashLine(membersCapital, 25_000m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: "CONT-001",
            Memo: "Owner contribution");

        PostEntryRequest entry = CashPosting.ComposeDeposit(id, body, accounts);

        // Dr Cash = total
        PostLineRequest cashLine = entry.Lines.Single(l => l.AccountId == cash);
        Assert.Equal("Debit", cashLine.Direction);
        Assert.Equal(25_000m, cashLine.Amount);

        // Cr the line account
        PostLineRequest capitalLine = entry.Lines.Single(l => l.AccountId == membersCapital);
        Assert.Equal("Credit", capitalLine.Direction);
        Assert.Equal(25_000m, capitalLine.Amount);

        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
    }

    // Test 4a: empty lines → ArgumentException.
    [Fact]
    public void ComposeDisbursement_empty_lines_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var body = new CashDisbursementBody(
            Lines: [],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDisbursement(id, body, accounts));
    }

    // Test 4b: zero amount → ArgumentException.
    [Fact]
    public void ComposeDisbursement_zero_amount_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var body = new CashDisbursementBody(
            Lines: [new CashLine(Guid.NewGuid(), 0m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDisbursement(id, body, accounts));
    }

    // Test 4c: negative amount → ArgumentException.
    [Fact]
    public void ComposeDisbursement_negative_amount_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var body = new CashDisbursementBody(
            Lines: [new CashLine(Guid.NewGuid(), -100m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDisbursement(id, body, accounts));
    }

    // Test 4d: line account == CashAccountId → ArgumentException.
    [Fact]
    public void ComposeDisbursement_line_account_equals_cash_account_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out Guid cash);
        var body = new CashDisbursementBody(
            Lines: [new CashLine(cash, 500m)],  // same as cash account
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDisbursement(id, body, accounts));
    }

    // Test 4e: deposit with empty lines → ArgumentException.
    [Fact]
    public void ComposeDeposit_empty_lines_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var body = new CashDepositBody(
            Lines: [],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDeposit(id, body, accounts));
    }

    // Test 4f: deposit with line account == CashAccountId → ArgumentException.
    [Fact]
    public void ComposeDeposit_line_account_equals_cash_account_throws_ArgumentException()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out Guid cash);
        var body = new CashDepositBody(
            Lines: [new CashLine(cash, 500m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        Assert.Throws<ArgumentException>(() => CashPosting.ComposeDeposit(id, body, accounts));
    }

    // Test 5a: SourceType is "CashDisbursement" and EntryIdentity.ForSource id is stable.
    [Fact]
    public void ComposeDisbursement_carries_correct_SourceType_and_deterministic_Id()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var body = new CashDisbursementBody(
            Lines: [new CashLine(Guid.NewGuid(), 100m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        PostEntryRequest a = CashPosting.ComposeDisbursement(id, body, accounts);
        PostEntryRequest b = CashPosting.ComposeDisbursement(id, body, accounts);

        Assert.Equal("CashDisbursement", a.SourceType);
        Assert.Equal(id, a.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(CashPosting.DisbursementSourceType, id), a.Id);
        Assert.Equal(a.Id, b.Id); // deterministic across calls
    }

    // Test 5b: SourceType is "CashDeposit" and EntryIdentity.ForSource id is stable and distinct from disbursement.
    [Fact]
    public void ComposeDeposit_carries_correct_SourceType_and_deterministic_Id_distinct_from_disbursement()
    {
        Guid id = Guid.NewGuid();
        CashPostingAccounts accounts = MakeAccounts(out _);
        var depositBody = new CashDepositBody(
            Lines: [new CashLine(Guid.NewGuid(), 100m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);
        var disbursementBody = new CashDisbursementBody(
            Lines: [new CashLine(Guid.NewGuid(), 100m)],
            Date: new DateOnly(2026, 6, 26),
            Reference: null,
            Memo: null);

        PostEntryRequest deposit1 = CashPosting.ComposeDeposit(id, depositBody, accounts);
        PostEntryRequest deposit2 = CashPosting.ComposeDeposit(id, depositBody, accounts);
        PostEntryRequest disbursement = CashPosting.ComposeDisbursement(id, disbursementBody, accounts);

        Assert.Equal("CashDeposit", deposit1.SourceType);
        Assert.Equal(id, deposit1.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(CashPosting.DepositSourceType, id), deposit1.Id);
        Assert.Equal(deposit1.Id, deposit2.Id); // deterministic
        Assert.NotEqual(deposit1.Id, disbursement.Id); // distinct source types produce distinct ids
    }
}
