using Accounting101.Settlement;

namespace Accounting101.Settlement.Tests;

public sealed class SettlementTests
{
    [Theory]
    [InlineData(100, 0, 100, SettlementStatus.Open)]
    [InlineData(100, 40, 60, SettlementStatus.PartiallyPaid)]
    [InlineData(100, 100, 0, SettlementStatus.Paid)]
    [InlineData(100, 120, -20, SettlementStatus.Paid)]
    public void Derives_open_balance_and_status(decimal total, decimal applied, decimal expectedOpen, SettlementStatus expectedStatus)
    {
        Assert.Equal(expectedOpen, Accounting101.Settlement.Settlement.OpenBalance(total, applied));
        Assert.Equal(expectedStatus, Accounting101.Settlement.Settlement.Status(total, applied));
    }

    [Fact]
    public void Allocation_carries_a_generic_target_id()
    {
        Guid target = Guid.NewGuid();
        Allocation a = new(target, 50m);
        Assert.Equal(target, a.TargetId);
        Assert.Equal(50m, a.Amount);
    }
}
