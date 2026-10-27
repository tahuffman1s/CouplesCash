using System.Globalization;

namespace CouplesCash.Api.Domain;

/// <summary>What the scheduler needs to know about a recurring item to decide on autopay.</summary>
public readonly record struct AutopayCandidate(Guid ItemId, bool AutoPay, Recurrence Rule);

/// <summary>An occurrence the scheduler should auto-mark paid.</summary>
public readonly record struct AutopayDue(Guid ItemId, DateOnly DueDate);

/// <summary>An unpaid occurrence a user should be reminded about.</summary>
public readonly record struct ReminderCandidate(Guid ItemId, string Name, long AmountCents, DateOnly DueDate);

/// <summary>A week summary that is due today: its kind and the <c>week_summary_log.week_start</c> it is keyed on.</summary>
public readonly record struct WeekSummaryDue(WeekSummaryKind Kind, DateOnly WeekStart);

/// <summary>
/// Pure selection logic for <c>SchedulerService</c>: which occurrences to auto-pay, whether it is a
/// user's reminder hour, which due dates to look at, and the text of a reminder. Keeping this free of
/// I/O makes the background job trivially unit-testable.
/// </summary>
public static class SchedulerRules
{
    /// <summary>Autopay items with an occurrence due in <c>[today − lookbackDays, today]</c>, excluding those already handled.</summary>
    public static IReadOnlyList<AutopayDue> AutopayDue(
        IEnumerable<AutopayCandidate> items,
        DateOnly today,
        int lookbackDays = 0,
        IReadOnlySet<(Guid ItemId, DateOnly DueDate)>? alreadyHandled = null)
    {
        var from = today.AddDays(-Math.Max(0, lookbackDays));
        var result = new List<AutopayDue>();
        foreach (var item in items.Where(i => i.AutoPay))
        {
            foreach (var due in RecurrenceEngine.Expand(item.Rule, from, today))
            {
                if (alreadyHandled is null || !alreadyHandled.Contains((item.ItemId, due)))
                {
                    result.Add(new AutopayDue(item.ItemId, due));
                }
            }
        }

        return result;
    }

    /// <summary>Autopay items due exactly today.</summary>
    public static IReadOnlyList<AutopayDue> AutopayDueToday(IEnumerable<AutopayCandidate> items, DateOnly today) =>
        AutopayDue(items, today, 0);

    /// <summary>True during the user's chosen local hour. The reminder log keeps it to one send per occurrence.</summary>
    public static bool ShouldSendReminderNow(bool remindersEnabled, int hourLocal, DateTime nowLocal) =>
        remindersEnabled && nowLocal.Hour == hourLocal;

    /// <summary>The due dates to remind about: <c>today + d</c> for each configured <c>daysBefore</c> (deduplicated, ascending).</summary>
    public static IReadOnlyList<DateOnly> ReminderDates(DateOnly todayLocal, IEnumerable<int> daysBefore) =>
        daysBefore.Where(d => d is >= 0 and <= 31).Distinct().OrderBy(d => d).Select(todayLocal.AddDays).ToList();

    /// <summary>Unpaid outflow occurrences on the reminder dates that have not been sent to this user yet.</summary>
    public static IReadOnlyList<ReminderCandidate> DueReminders(
        IEnumerable<ReminderCandidate> unpaidOccurrences,
        IReadOnlyList<DateOnly> reminderDates,
        IReadOnlySet<(Guid ItemId, DateOnly DueDate)> alreadySent)
    {
        var dates = reminderDates.ToHashSet();
        return unpaidOccurrences
            .Where(o => dates.Contains(o.DueDate) && !alreadySent.Contains((o.ItemId, o.DueDate)))
            .OrderBy(o => o.DueDate)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"Electric is due tomorrow · $142.50", "Rent is due today · $1,850.00", "Water is due in 3 days (Fri Nov 6) · $40.00".</summary>
    public static string ReminderBody(string name, long amountCents, string currency, DateOnly dueDate, DateOnly today)
    {
        var days = dueDate.DayNumber - today.DayNumber;
        var when = days switch
        {
            <= 0 => "today",
            1 => "tomorrow",
            _ => $"in {days} days ({dueDate.ToString("ddd MMM d", CultureInfo.InvariantCulture)})",
        };
        return $"{name} is due {when} · {Money.Format(amountCents, currency)}";
    }

    /// <summary>"Rent auto-paid · $1,850.00"</summary>
    public static string AutopayBody(string name, long amountCents, string currency) =>
        $"{name} auto-paid · {Money.Format(amountCents, currency)}";

    /// <summary>Deep link that opens the calendar on a day: <c>/calendar?m=YYYY-MM&amp;d=YYYY-MM-DD</c>.</summary>
    public static string CalendarLink(DateOnly date) =>
        $"/calendar?m={date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}&d={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>Converts UTC to the household's local time; unknown or empty zone ids fall back to UTC.</summary>
    public static DateTime ToLocal(DateTime utc, string? timezoneId)
    {
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(timezoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timezoneId, out var tz))
        {
            return TimeZoneInfo.ConvertTimeFromUtc(instant, tz);
        }

        return instant;
    }

    /// <summary>The UTC instant at which <paramref name="date"/> begins in the household's time zone (UTC midnight for unknown zones).</summary>
    public static DateTime LocalMidnightUtc(DateOnly date, string? timezoneId)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (!string.IsNullOrWhiteSpace(timezoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timezoneId, out var tz))
        {
            return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
        }

        return DateTime.SpecifyKind(midnight, DateTimeKind.Utc);
    }

    // ---------------------------------------------------------------------------------------------
    // v4: week summaries
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Which week summary is due on <paramref name="todayLocal"/> for a household with <paramref name="weekStart"/> (0 Sunday / 1 Monday):
    /// <c>ahead</c> on the day before the week starts (the last day of the current week; keyed on the coming week's start),
    /// <c>left</c> on the 5th day of the week (Friday for Monday start, Thursday for Sunday start; keyed on the current week's start).
    /// Null on every other day. The two can never fall on the same day.
    /// </summary>
    public static WeekSummaryDue? WeekSummaryDueToday(DateOnly todayLocal, int weekStart)
    {
        var week = WeekEngine.WeekBounds(todayLocal, weekStart);
        if (todayLocal == week.End)
        {
            return new WeekSummaryDue(WeekSummaryKind.Ahead, week.End.AddDays(1));
        }

        if (todayLocal == week.Start.AddDays(4))
        {
            return new WeekSummaryDue(WeekSummaryKind.Left, week.Start);
        }

        return null;
    }

    /// <summary>"Week ahead: $60.00 planned, $2,390.00 expected left" (the second part is dropped when there is no projection).</summary>
    public static string WeekAheadBody(long plannedCents, long? leftCents, string currency) =>
        leftCents is { } left
            ? $"Week ahead: {Money.Format(plannedCents, currency)} planned, {Money.Format(left, currency)} expected left"
            : $"Week ahead: {Money.Format(plannedCents, currency)} planned";

    /// <summary>
    /// "You have $2,390.00 left this week · budget $4.20 over" / "… · budget $12.00 under" / "… · budget on target".
    /// Without a weekly budget the budget part is dropped; without a left-at-end figure the body starts with "Weekly check-in".
    /// </summary>
    public static string WeekLeftBody(long? leftCents, long? budgetVarianceCents, string currency)
    {
        var head = leftCents is { } left ? $"You have {Money.Format(left, currency)} left this week" : "Weekly check-in";
        return budgetVarianceCents switch
        {
            null => head,
            0 => $"{head} · budget on target",
            > 0 => $"{head} · budget {Money.Format(budgetVarianceCents.Value, currency)} over",
            _ => $"{head} · budget {Money.Format(-budgetVarianceCents.Value, currency)} under",
        };
    }

    /// <summary>Push tag per kind: <c>week-ahead</c> / <c>week-left</c>; the URL is always <see cref="WeekLink"/>.</summary>
    public static string WeekSummaryTag(WeekSummaryKind kind) => kind == WeekSummaryKind.Ahead ? "week-ahead" : "week-left";

    public const string WeekLink = "/week";

    // ---------------------------------------------------------------------------------------------
    // v7: the payday push
    // ---------------------------------------------------------------------------------------------

    /// <summary>Push tag of the payday push (one per pay week; a later push replaces an earlier one on the device).</summary>
    public const string PaydayTag = "payday";

    /// <summary>Push tag of the sweep-day push ("move $X to savings today").</summary>
    public const string SweepTag = "sweep";

    /// <summary>
    /// "Payday · fun money $40.00" (values only): fun money gets " sent ✓" once every fun transfer of the checklist is paid and is dropped
    /// when there is none. The savings move is not on payday any more — it has its own push on the sweep day.
    /// </summary>
    public static string PaydayBody(long funCents, bool funSent, string currency)
    {
        var parts = new List<string> { "Payday" };
        if (funCents > 0)
        {
            parts.Add($"fun money {Money.Format(funCents, currency)}{(funSent ? " sent ✓" : "")}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Move $253.17 to savings today" — what is left at the end of the pay week (values only).</summary>
    public static string SweepBody(long plannedCents, string currency) => $"Move {Money.Format(plannedCents, currency)} to savings today";

    /// <summary>The sweep day of the pay week containing <paramref name="today"/> is today (the day before payday).</summary>
    public static bool IsSweepDay(DateOnly today, int paydayWeekday) => ((int)today.DayOfWeek + 1) % 7 == paydayWeekday;
}
