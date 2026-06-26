namespace Accounting101.Payables.Tests;

public sealed class BillTypesTests
{
    [Fact]
    public void Bill_total_sums_its_lines()
    {
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), BillDate = new DateOnly(2026, 3, 1),
            Status = BillStatus.Draft,
            Lines =
            [
                new BillLine { Description = "Rent", Amount = 6000m, ExpenseAccountId = Guid.NewGuid() },
                new BillLine { Description = "Utilities", Amount = 800m, ExpenseAccountId = Guid.NewGuid() },
            ],
        };
        Assert.Equal(6800m, bill.Total);
    }
}
