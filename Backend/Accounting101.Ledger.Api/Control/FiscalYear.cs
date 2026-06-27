namespace Accounting101.Ledger.Api.Control;

/// <summary>Fiscal-year-end helpers. A fiscal year ends on the last day of a configured month.</summary>
public static class FiscalYear
{
    /// <summary>The client's fiscal-year-end month (1-12), defaulting to December (12) for legacy
    /// registrations whose field deserialized to 0 (or any out-of-range value).</summary>
    public static int MonthOf(ClientRegistration client) =>
        client.FiscalYearEndMonth is >= 1 and <= 12 ? client.FiscalYearEndMonth : 12;

    /// <summary>The fiscal-year-end date for a given year: the last calendar day of <paramref name="month"/>.</summary>
    public static DateOnly EndDateFor(int month, int year) =>
        new(year, month, DateTime.DaysInMonth(year, month));
}
