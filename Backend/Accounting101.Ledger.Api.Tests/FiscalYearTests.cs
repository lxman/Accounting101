using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FiscalYearTests
{
    [Theory]
    [InlineData(12, 2024, "2024-12-31")]
    [InlineData(6, 2024, "2024-06-30")]
    [InlineData(2, 2024, "2024-02-29")]   // leap year
    [InlineData(2, 2025, "2025-02-28")]
    public void EndDateFor_is_the_last_day_of_the_month(int month, int year, string expected)
        => Assert.Equal(DateOnly.Parse(expected), FiscalYear.EndDateFor(month, year));

    [Theory]
    [InlineData(6, 6)]
    [InlineData(0, 12)]    // legacy registration (field absent -> 0) defaults to December
    [InlineData(13, 12)]   // out of range -> December
    public void MonthOf_normalizes_to_a_valid_month_defaulting_to_December(int stored, int expected)
        => Assert.Equal(expected, FiscalYear.MonthOf(new ClientRegistration { FiscalYearEndMonth = stored }));
}
