using System.Globalization;

namespace CouplesCash.Api.Domain;

/// <summary>What the scheduler needs to know about a recurring item to decide on autopay.</summary>
public readonly record struct AutopayCandidate(Guid ItemId, bool AutoPay, Recurrence Rule);

/// <summary>An occurrence the scheduler should auto-mark paid.</summary>
public readonly record struct AutopayDue(Guid ItemId, DateOnly DueDate);

/// <summary>An unpaid occurrence a user should be reminded about.</summary>
public readonly record struct ReminderCandidate(Guid ItemId, string Name, long AmountCents, DateOnly DueDate);

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
}
