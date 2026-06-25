using Accounting101.Receivables;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

public sealed class SettlementTests
{
    [Theory]
    [InlineData(100, 0, 100, SettlementStatus.Open)]
    [InlineData(100, 40, 60, SettlementStatus.PartiallyPaid)]
    [InlineData(100, 100, 0, SettlementStatus.Paid)]
    [InlineData(100, 120, -20, SettlementStatus.Paid)] // over-applied still reads Paid
    public void Derives_open_balance_and_status(decimal total, decimal applied, decimal expectedOpen, SettlementStatus expectedStatus)
    {
        Assert.Equal(expectedOpen, Accounting101.Settlement.Settlement.OpenBalance(total, applied));
        Assert.Equal(expectedStatus, Accounting101.Settlement.Settlement.Status(total, applied));
    }
}
