using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class BillPaymentTypesTests
{
    [Fact]
    public void BillPayment_computes_allocated_and_unapplied()
    {
        BillPayment p = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31), Amount = 500m,
            Allocations = [new Allocation(Guid.NewGuid(), 300m)],
        };
        Assert.Equal(300m, p.Allocated);
        Assert.Equal(200m, p.Unapplied);
        Assert.False(p.Voided);
    }

    [Fact]
    public void VendorCreditApplication_computes_applied()
    {
        VendorCreditApplication c = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = new DateOnly(2026, 4, 1),
            Allocations = [new Allocation(Guid.NewGuid(), 50m), new Allocation(Guid.NewGuid(), 25m)],
        };
        Assert.Equal(75m, c.Applied);
    }
}
