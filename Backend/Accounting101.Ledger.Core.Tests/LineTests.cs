using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class LineTests
{
    private static Line Line(Direction direction, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Direction = direction,
        Amount = amount,
    };

    [Fact]
    public void SignedEffect_is_debit_positive_credit_negative()
    {
        Assert.Equal(100m, Line(Direction.Debit, 100m).SignedEffect);
        Assert.Equal(-100m, Line(Direction.Credit, 100m).SignedEffect);
    }

    [Fact]
    public void SignedEffect_honours_the_amount_sign()
    {
        // A negative debit nets like a credit; a negative credit nets like a debit.
        Assert.Equal(-100m, Line(Direction.Debit, -100m).SignedEffect);
        Assert.Equal(100m, Line(Direction.Credit, -100m).SignedEffect);
    }
}
