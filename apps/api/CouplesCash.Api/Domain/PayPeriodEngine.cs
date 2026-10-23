namespace CouplesCash.Api.Domain;

/// <summary>The pay period that contains a date: <c>[Start, End]</c> inclusive, <c>NextPayday = End + 1</c>.</summary>
public readonly record struct PayPeriodWindow(DateOnly Start, DateOnly End, DateOnly NextPayday)
{
    /// <summary>Whole days from <paramref name="date"/> until the next payday (0 on payday itself).</summary>
    public int DaysLeft(DateOnly date) => NextPayday.DayNumber - date.DayNumber;
}

/// <summary>
/// Pure pay-period math.
/// <list type="bullet">
/// <item><c>weekly</c>: paydays every 7 days from the anchor (before and after it).</item>
/// <item><c>biweekly</c>: every 14 days from the anchor.</item>
/// <item><c>monthly</c>: the anchor's day of month, clamped to the last day of shorter months.</item>
/// <item><c>bimonthly</c>: the anchor day and anchor day + 15 (both clamped); when the anchor day is &gt; 15 the other payday is anchor − 15.</item>
/// </list>
/// <c>Start</c> is the most recent payday on or before the date, <c>End</c> the day before the next payday.
/// </summary>
public static class PayPeriodEngine
{
    public static PayPeriodWindow Resolve(PayPeriod period, DateOnly anchor, DateOnly date) => period switch
    {
        PayPeriod.Weekly => FixedStep(anchor, date, 7),
        PayPeriod.Biweekly => FixedStep(anchor, date, 14),
        PayPeriod.Monthly => Calendar(date, [anchor.Day]),
        PayPeriod.Bimonthly => Calendar(date, BimonthlyDays(anchor.Day)),
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown pay period"),
    };

    /// <summary>The two days of the month a bimonthly household is paid on, ascending.</summary>
    public static int[] BimonthlyDays(int anchorDay)
    {
        var other = anchorDay > 15 ? anchorDay - 15 : anchorDay + 15;
        return anchorDay < other ? [anchorDay, other] : [other, anchorDay];
    }

    /// <summary>Paydays of a given month for a set of anchor days, clamped to the month's length, ascending and distinct.</summary>
    public static IEnumerable<DateOnly> PaydaysInMonth(int year, int month, IReadOnlyList<int> days)
    {
        var last = DateTime.DaysInMonth(year, month);
        return days.Select(d => new DateOnly(year, month, Math.Min(d, last))).Distinct().OrderBy(d => d);
    }

    private static PayPeriodWindow FixedStep(DateOnly anchor, DateOnly date, int step)
    {
        var offset = date.DayNumber - anchor.DayNumber;
        var k = FloorDiv(offset, step);
        var start = anchor.AddDays(k * step);
        var next = start.AddDays(step);
        return new PayPeriodWindow(start, next.AddDays(-1), next);
    }

    private static PayPeriodWindow Calendar(DateOnly date, int[] days)
    {
        var previousMonth = date.AddMonths(-1);
        var nextMonth = date.AddMonths(1);
        var paydays = PaydaysInMonth(previousMonth.Year, previousMonth.Month, days)
            .Concat(PaydaysInMonth(date.Year, date.Month, days))
            .Concat(PaydaysInMonth(nextMonth.Year, nextMonth.Month, days))
            .ToList();

        var start = paydays.Last(p => p <= date);
        var next = paydays.First(p => p > date);
        return new PayPeriodWindow(start, next.AddDays(-1), next);
    }

    private static int FloorDiv(int a, int b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }
}
