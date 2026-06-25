using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentTypesTests
{
    [Fact]
    public void Payment_computes_allocated_and_unapplied()
    {
        Payment p = new()
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31), Amount = 500m,
            Allocations = [new Allocation(Guid.NewGuid(), 300m), new Allocation(Guid.NewGuid(), 100m)],
        };
        Assert.Equal(400m, p.Allocated);
        Assert.Equal(100m, p.Unapplied);
        Assert.False(p.Voided);
    }

    [Fact]
    public void CreditApplication_computes_applied()
    {
        CreditApplication c = new()
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31),
            Allocations = [new Allocation(Guid.NewGuid(), 75m), new Allocation(Guid.NewGuid(), 25m)],
        };
        Assert.Equal(100m, c.Applied);
    }
}
