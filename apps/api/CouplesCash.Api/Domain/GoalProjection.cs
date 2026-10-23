namespace CouplesCash.Api.Domain;

/// <summary>A point on a goal's history: the saved amount at a moment in time.</summary>
public readonly record struct GoalPoint(DateTime At, long Value);

/// <summary>Pace, completion forecast and the amount needed per month to hit the target date.</summary>
public sealed record GoalForecast(long? MonthlyPaceCents, DateOnly? ProjectedDate, long? MonthlyNeededCents)
{
    public static readonly GoalForecast None = new(null, null, null);
}

/// <summary>
/// Pure savings-goal projection.
/// <list type="bullet">
/// <item><c>monthlyPaceCents</c> = (latest − earliest value within the last 90 days) ÷ months elapsed; null with fewer than two points or less than 7 days between them.</item>
/// <item><c>projectedDate</c> = today + (target − current) ÷ pace; null when the pace is not positive or the target is already reached.</item>
/// <item><c>monthlyNeededCents</c> = (target − current) ÷ months until the target date (rounded up); null without a target date or when reached. A target date in the past needs everything now.</item>
/// </list>
/// A month is 30.4375 days (365.25 ÷ 12).
/// </summary>
public static class GoalProjection
{
    public const int WindowDays = 90;
    public const int MinSpanDays = 7;
    public const decimal DaysPerMonth = 30.4375m;

    public static GoalForecast Project(IReadOnlyList<GoalPoint> history, long targetCents, long currentCents, DateOnly? targetDate, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var remaining = targetCents - currentCents;
        var reached = remaining <= 0;

        var pace = MonthlyPace(history, nowUtc);

        DateOnly? projected = null;
        if (!reached && pace is > 0)
        {
            var months = remaining / (decimal)pace.Value;
            var days = (int)Math.Ceiling(months * DaysPerMonth);
            projected = today.AddDays(Math.Min(days, 365 * 100));
        }

        long? needed = null;
        if (!reached && targetDate is { } target)
        {
            var daysLeft = target.DayNumber - today.DayNumber;
            needed = daysLeft <= 0
                ? remaining
                : (long)Math.Ceiling(remaining / (daysLeft / DaysPerMonth));
        }

        return new GoalForecast(pace, projected, needed);
    }

    /// <summary>Average change per month over the points recorded in the last <see cref="WindowDays"/> days.</summary>
    public static long? MonthlyPace(IReadOnlyList<GoalPoint> history, DateTime nowUtc)
    {
        var since = nowUtc.AddDays(-WindowDays);
        var window = history.Where(p => p.At >= since && p.At <= nowUtc).OrderBy(p => p.At).ToList();
        if (window.Count < 2)
        {
            return null;
        }

        var first = window[0];
        var last = window[^1];
        var span = last.At - first.At;
        if (span.TotalDays < MinSpanDays)
        {
            return null;
        }

        var months = (decimal)span.TotalDays / DaysPerMonth;
        return (long)Math.Round((last.Value - first.Value) / months, 0, MidpointRounding.ToEven);
    }
}
