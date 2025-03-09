using System;

namespace Accounting101.Angular.DataAccess.Models;

public class DateRange(DateOnly start, DateOnly end)
{
    public DateOnly Start { get; } = start;

    public DateOnly End { get; } = end;

    public DateRange(DateOnly start, TimeSpan duration) : this(start, start.AddDays(duration.Days))
    {
    }

    public DateRange(DateOnly start, int days) : this(start, start.AddDays(days))
    {
    }

    public bool Contains(DateOnly date) => date >= Start && date <= End;
}