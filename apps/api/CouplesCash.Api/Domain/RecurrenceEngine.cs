namespace CouplesCash.Api.Domain;

/// <summary>The part of a recurring item that determines when it occurs.</summary>
public readonly record struct Recurrence(Frequency Frequency, DateOnly AnchorDate, DateOnly? EndDate);

/// <summary>
/// Pure recurrence math. Occurrences are generated from <see cref="Recurrence.AnchorDate"/> stepping by
/// frequency. Monthly, quarterly and yearly steps are always computed from the anchor (never from the
/// previous occurrence), so a 31st anchor clamps to the 28th/29th/30th in short months and springs back
/// to the 31st afterwards. <c>once</c> yields exactly one occurrence.
/// </summary>
public static class RecurrenceEngine
{
    /// <summary>All occurrences with <c>from &lt;= date &lt;= to</c>, in ascending order.</summary>
    public static IEnumerable<DateOnly> Expand(Recurrence rule, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            yield break;
        }

        var last = rule.EndDate is { } end && end < to ? end : to;
        if (last < rule.AnchorDate)
        {
            yield break;
        }

        if (rule.Frequency == Frequency.Once)
        {
            if (rule.AnchorDate >= from)
            {
                yield return rule.AnchorDate;
            }

            yield break;
        }

        for (var n = FirstIndexAtOrBefore(rule, from); ; n++)
        {
            var date = Nth(rule, n);
            if (date > last)
            {
                yield break;
            }

            if (date >= from)
            {
                yield return date;
            }
        }
    }

    /// <summary>True when <paramref name="date"/> is a genuine occurrence of the rule.</summary>
    public static bool IsOccurrence(Recurrence rule, DateOnly date) => Expand(rule, date, date).Any();

    /// <summary>
    /// The date one step before the anchor, where a payment older than the item can be linked (the item then starts there). Null for
    /// <c>once</c>, an item that ended before it started, and a month-end anchor the earlier month would clamp (Oct 31 → Sep 30 would move
    /// every later date to the 30th).
    /// </summary>
    public static DateOnly? EarlierDate(Recurrence rule)
    {
        if (rule.Frequency == Frequency.Once || rule.EndDate is { } end && end < rule.AnchorDate)
        {
            return null;
        }

        var earlier = Nth(rule, -1);
        return rule.Frequency is Frequency.Weekly or Frequency.Biweekly || earlier.Day == rule.AnchorDate.Day ? earlier : null;
    }

    /// <summary>A due date a transaction can be linked to: an occurrence, or the <see cref="EarlierDate"/>.</summary>
    public static bool IsLinkable(Recurrence rule, DateOnly date) => IsOccurrence(rule, date) || EarlierDate(rule) == date;

    /// <summary>
    /// The start after the payment on <paramref name="dueDate"/> was unlinked: the next occurrence when that payment was on the start date and
    /// the start is before the item was created (so it was only there because of the payment); null to keep the start.
    /// </summary>
    public static DateOnly? StartAfterUnlink(Recurrence rule, DateOnly dueDate, DateOnly createdOn)
    {
        if (rule.Frequency == Frequency.Once || dueDate != rule.AnchorDate || dueDate >= createdOn)
        {
            return null;
        }

        var next = Nth(rule, 1);
        return rule.EndDate is { } end && next > end ? null : next;
    }

    /// <summary>The first occurrence on or after <paramref name="today"/>, or null when there is none.</summary>
    public static DateOnly? NextDue(Recurrence rule, DateOnly today)
    {
        if (rule.EndDate is { } end && end < today)
        {
            return null;
        }

        if (rule.Frequency == Frequency.Once)
        {
            return rule.AnchorDate >= today ? rule.AnchorDate : null;
        }

        for (var n = FirstIndexAtOrBefore(rule, today); ; n++)
        {
            var date = Nth(rule, n);
            if (rule.EndDate is { } e && date > e)
            {
                return null;
            }

            if (date >= today)
            {
                return date;
            }
        }
    }

    /// <summary>
    /// Monthly equivalent of an amount: weekly ×52/12, biweekly ×26/12, monthly ×1, quarterly ÷3,
    /// yearly ÷12, once → 0. Rounded half-to-even to whole cents.
    /// </summary>
    public static long MonthlyEquivalent(long amountCents, Frequency frequency)
    {
        decimal monthly = frequency switch
        {
            Frequency.Weekly => amountCents * 52m / 12m,
            Frequency.Biweekly => amountCents * 26m / 12m,
            Frequency.Monthly => amountCents,
            Frequency.Quarterly => amountCents / 3m,
            Frequency.Yearly => amountCents / 12m,
            Frequency.Once => 0m,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown frequency"),
        };

        return (long)Math.Round(monthly, 0, MidpointRounding.ToEven);
    }

    /// <summary>The n-th occurrence (0 = anchor), computed from the anchor so month-end clamping is per occurrence.</summary>
    public static DateOnly Nth(Recurrence rule, int n)
    {
        var anchor = rule.AnchorDate;
        return rule.Frequency switch
        {
            Frequency.Weekly => anchor.AddDays(7 * n),
            Frequency.Biweekly => anchor.AddDays(14 * n),
            Frequency.Monthly => anchor.AddMonths(n),
            Frequency.Quarterly => anchor.AddMonths(3 * n),
            Frequency.Yearly => anchor.AddYears(n),
            Frequency.Once => anchor,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Frequency, "Unknown frequency"),
        };
    }

    /// <summary>
    /// A cheap lower bound for the index of the first occurrence at or after <paramref name="from"/>,
    /// so we never iterate from the anchor through years of history.
    /// </summary>
    private static int FirstIndexAtOrBefore(Recurrence rule, DateOnly from)
    {
        var anchor = rule.AnchorDate;
        if (from <= anchor)
        {
            return 0;
        }

        var months = ((from.Year - anchor.Year) * 12) + from.Month - anchor.Month;
        var days = from.DayNumber - anchor.DayNumber;

        return rule.Frequency switch
        {
            Frequency.Weekly => days / 7,
            Frequency.Biweekly => days / 14,
            Frequency.Monthly => Math.Max(0, months),
            Frequency.Quarterly => Math.Max(0, months / 3),
            Frequency.Yearly => Math.Max(0, from.Year - anchor.Year),
            _ => 0,
        };
    }
}
