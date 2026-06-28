using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AdjustmentPostingTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Offset = Guid.NewGuid();
    private static readonly DateOnly D = new(2026, 1, 31);

    private static BankAdjustmentBody Body(AdjustmentKind kind, decimal amount = 5m) =>
        new(Guid.NewGuid(), Cash, Offset, kind, amount, D, "bank fee");

    [Fact]
    public void Charge_debits_the_offset_and_credits_cash()
    {
        Guid id = Guid.NewGuid();
        PostEntryRequest e = AdjustmentPosting.Compose(id, Body(AdjustmentKind.Charge));
        Assert.Equal(EntryIdentity.ForSource("BankAdjustment", id), e.Id);
        Assert.Equal("BankAdjustment", e.SourceType);
        Assert.Equal(id, e.SourceRef);
        Assert.Equal(D, e.EffectiveDate);
        Assert.Contains(e.Lines, l => l.AccountId == Offset && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(e.Lines, l => l.AccountId == Cash && l.Direction == "Credit" && l.Amount == 5m);
    }

    [Fact]
    public void Credit_debits_cash_and_credits_the_offset()
    {
        PostEntryRequest e = AdjustmentPosting.Compose(Guid.NewGuid(), Body(AdjustmentKind.Credit));
        Assert.Contains(e.Lines, l => l.AccountId == Cash && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(e.Lines, l => l.AccountId == Offset && l.Direction == "Credit" && l.Amount == 5m);
    }

    [Fact]
    public void A_non_positive_amount_is_rejected() =>
        Assert.Throws<ArgumentException>(() => AdjustmentPosting.Compose(Guid.NewGuid(), Body(AdjustmentKind.Charge, 0m)));

    [Fact]
    public void An_offset_equal_to_cash_is_rejected()
    {
        BankAdjustmentBody body = new(Guid.NewGuid(), Cash, Cash, AdjustmentKind.Charge, 5m, D, null);
        Assert.Throws<ArgumentException>(() => AdjustmentPosting.Compose(Guid.NewGuid(), body));
    }
}
