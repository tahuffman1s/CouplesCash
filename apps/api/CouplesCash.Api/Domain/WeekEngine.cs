using CouplesCash.Api.Contracts;

namespace CouplesCash.Api.Domain;

/// <summary>A week: <c>[Start, End]</c> inclusive, <c>End = Start + 6</c>.</summary>
public readonly record struct WeekWindow(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>The week <paramref name="weeks"/> weeks later (negative = earlier).</summary>
    public WeekWindow Shift(int weeks) => new(Start.AddDays(7 * weeks), End.AddDays(7 * weeks));
}

/// <summary>
/// An occurrence as the week math sees it. <paramref name="AmountCents"/> is the planned amount; <paramref name="PaidCents"/>
/// the actual when paid (any source), else null.
/// </summary>
public sealed record WeekOccurrence(Guid ItemId, Kind Kind, long AmountCents, DateOnly DueDate, long? PaidCents, Guid? CategoryId = null)
{
    public bool IsPaid => PaidCents is not null;

    /// <summary>What actually moved (or will move): the paid amount when cleared, else the plan.</summary>
    public long ActualCents => PaidCents ?? AmountCents;
}

/// <summary>An expense (unplanned spending) as the week math sees it.</summary>
public sealed record WeekExpense(DateOnly SpentOn, long AmountCents, Guid? CategoryId = null);

/// <summary>A category that has a weekly budget.</summary>
public sealed record WeekBudgetCategory(Guid Id, string Name, string Color, string Icon, long BudgetCents);

/// <summary>The headline numbers of one week.</summary>
public readonly record struct WeekTotals(
    long IncomeExpectedCents,
    long IncomeReceivedCents,
    long PlannedOutCents,
    long PaidOutCents,
    long RemainingOutCents,
    long UnplannedCents);

/// <summary>
/// Pure week math for <c>GET /v1/week</c>, <c>GET /v1/budgets/week</c> and the scheduler's week summaries (PLAN-V4 "Concepts").
/// No I/O; the service loads occurrences, expenses, balances and budgets and hands them in. All amounts are integer cents.
/// <list type="bullet">
/// <item><b>Week</b> = <c>weekStart … weekStart + 6</c> using the household's <c>week_start</c> (0 Sunday / 1 Monday).</item>
/// <item><b>Status</b>: paid → <c>cleared</c>; unpaid and <c>dueDate &lt; today</c> → <c>overdue</c>; otherwise <c>planned</c>.</item>
/// <item><b>Variance</b> = <c>paid.amountCents − amountCents</c> when paid (positive = cost more than planned), else null.</item>
/// <item><b>Left at end of week</b>: see <see cref="LeftAtEnd"/>.</item>
/// <item><b>Weekly budgets</b>: see <see cref="SpentCents"/>, <see cref="CategoryRows"/>, <see cref="OverallRow"/>, <see cref="History"/>.</item>
/// </list>
/// </summary>
public static class WeekEngine
{
    /// <summary>Label, colour and icon of the overall weekly target row (<c>categoryId = null</c>).</summary>
    public const string OverallName = "Weekly spending";
    public const string OverallColor = "#6366f1";
    public const string OverallIcon = "wallet";

    /// <summary>Default number of weeks in <see cref="History"/> (the viewed week plus the seven before it).</summary>
    public const int HistoryWeeks = 8;

    /// <summary>
    /// The week containing <paramref name="date"/>. <paramref name="weekStart"/> is 0 (Sunday) or 1 (Monday):
    /// <c>start = date − ((dayOfWeek − weekStart + 7) mod 7)</c>, <c>end = start + 6</c>.
    /// </summary>
    public static WeekWindow WeekBounds(DateOnly date, int weekStart)
    {
        if (weekStart is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(weekStart), weekStart, "Week start must be 0 (Sunday) or 1 (Monday)");
        }

        var offset = ((int)date.DayOfWeek - weekStart + 7) % 7;
        var start = date.AddDays(-offset);
        return new WeekWindow(start, start.AddDays(6));
    }

    /// <summary>The week contains today.</summary>
    public static bool IsCurrent(WeekWindow week, DateOnly today) => week.Contains(today);

    /// <summary>The week ended before today.</summary>
    public static bool IsPast(WeekWindow week, DateOnly today) => week.End < today;

    /// <summary>The week starts after today.</summary>
    public static bool IsFuture(WeekWindow week, DateOnly today) => week.Start > today;

    /// <summary>cleared when paid; overdue when unpaid and <c>dueDate &lt; today</c>; planned otherwise (including unpaid and due today).</summary>
    public static OccurrenceStatus Status(DateOnly dueDate, bool paid, DateOnly today)
    {
        if (paid)
        {
            return OccurrenceStatus.Cleared;
        }

        return dueDate < today ? OccurrenceStatus.Overdue : OccurrenceStatus.Planned;
    }

    /// <summary><c>paidCents − plannedCents</c> when paid (positive = cost more than planned), null when unpaid.</summary>
    public static long? Variance(long? paidCents, long plannedCents) => paidCents is { } paid ? paid - plannedCents : null;

    /// <summary>An unpaid occurrence due before the week and before today: shown once at the top of the week view, not under a day.</summary>
    public static bool IsOverdueBefore(DateOnly dueDate, bool paid, DateOnly weekStart, DateOnly today) =>
        !paid && dueDate < weekStart && dueDate < today;

    /// <summary>
    /// Headline numbers over the occurrences and expenses dated inside the week:
    /// <list type="bullet">
    /// <item><c>incomeExpected</c> = Σ planned amounts of income occurrences; <c>incomeReceived</c> = Σ actual amounts of paid income.</item>
    /// <item><c>plannedOut</c> = Σ planned amounts of bill/subscription occurrences; <c>paidOut</c> = Σ actual amounts of the paid ones;
    /// <c>remainingOut</c> = Σ planned amounts of the unpaid ones (so <c>paidOut + remainingOut ≠ plannedOut</c> when actuals differ from plans).</item>
    /// <item><c>unplanned</c> = Σ expenses in the week.</item>
    /// </list>
    /// </summary>
    public static WeekTotals Totals(WeekWindow week, IEnumerable<WeekOccurrence> occurrences, IEnumerable<WeekExpense> expenses)
    {
        long incomeExpected = 0, incomeReceived = 0, plannedOut = 0, paidOut = 0, remainingOut = 0;
        foreach (var o in occurrences.Where(o => week.Contains(o.DueDate)))
        {
            if (o.Kind.IsOutflow())
            {
                plannedOut += o.AmountCents;
                if (o.IsPaid)
                {
                    paidOut += o.ActualCents;
                }
                else
                {
                    remainingOut += o.AmountCents;
                }
            }
            else
            {
                incomeExpected += o.AmountCents;
                if (o.IsPaid)
                {
                    incomeReceived += o.ActualCents;
                }
            }
        }

        var unplanned = expenses.Where(e => week.Contains(e.SpentOn)).Sum(e => e.AmountCents);
        return new WeekTotals(incomeExpected, incomeReceived, plannedOut, paidOut, remainingOut, unplanned);
    }

    /// <summary>
    /// <c>spendableNow + Σ unpaid income due in [from, to] − Σ unpaid bill/subscription due in [from, to]</c> (planned amounts).
    /// Paid occurrences are already reflected in the balances and are skipped.
    /// </summary>
    public static long ProjectedLeft(long spendableNowCents, DateOnly from, DateOnly to, IEnumerable<WeekOccurrence> occurrences)
    {
        var left = spendableNowCents;
        foreach (var o in occurrences.Where(o => !o.IsPaid && o.DueDate >= from && o.DueDate <= to))
        {
            left += o.Kind.IsOutflow() ? -o.AmountCents : o.AmountCents;
        }

        return left;
    }

    /// <summary>
    /// Left at the end of the week:
    /// <list type="bullet">
    /// <item><b>Current week</b>: <c>spendableNow + unpaid income due in [today, end] − unpaid planned outflow due in [today, end]</c>
    /// (the rest of the week; unpaid items due earlier in the week are listed as overdue, not deducted again).</item>
    /// <item><b>Past week</b> (actuals): <c>startingBalance + incomeReceived − paidOut − unplanned</c>, where <c>startingBalance</c> is the
    /// sum of the latest spendable-account snapshots recorded before the week started; <b>null</b> when there is no such snapshot.</item>
    /// <item><b>Future week</b> (projected): <c>spendableNow + all unpaid income − all unpaid planned outflow due in [today, end]</c>.</item>
    /// </list>
    /// <paramref name="occurrences"/> must therefore cover <c>[min(today, start), end]</c>.
    /// </summary>
    public static long? LeftAtEnd(
        WeekWindow week,
        DateOnly today,
        long spendableNowCents,
        long? startingBalanceCents,
        IEnumerable<WeekOccurrence> occurrences,
        IEnumerable<WeekExpense> expenses)
    {
        if (IsPast(week, today))
        {
            if (startingBalanceCents is not { } starting)
            {
                return null;
            }

            var totals = Totals(week, occurrences, expenses);
            return starting + totals.IncomeReceivedCents - totals.PaidOutCents - totals.UnplannedCents;
        }

        return ProjectedLeft(spendableNowCents, today, week.End, occurrences);
    }

    /// <summary>
    /// Spent in the week for a category (<paramref name="categoryId"/> null = overall, every category and uncategorised):
    /// <c>Σ expenses dated in the week + Σ actual amounts of paid bill/subscription occurrences due in the week</c>.
    /// Unpaid occurrences and income never count as spent.
    /// </summary>
    public static long SpentCents(WeekWindow week, Guid? categoryId, IEnumerable<WeekOccurrence> occurrences, IEnumerable<WeekExpense> expenses)
    {
        var paidBills = occurrences
            .Where(o => o.IsPaid && o.Kind.IsOutflow() && week.Contains(o.DueDate) && (categoryId is null || o.CategoryId == categoryId))
            .Sum(o => o.ActualCents);
        var spent = expenses
            .Where(e => week.Contains(e.SpentOn) && (categoryId is null || e.CategoryId == categoryId))
            .Sum(e => e.AmountCents);
        return paidBills + spent;
    }

    /// <summary><c>varianceCents = spent − budget</c> (positive = over); <c>percent</c> is whole-number and uncapped (<see cref="BudgetMath.Percent"/>).</summary>
    public static WeekBudgetRowDto Row(Guid? categoryId, string name, string color, string icon, long budgetCents, long spentCents) =>
        new(categoryId, name, color, icon, budgetCents, spentCents, spentCents - budgetCents, BudgetMath.Percent(budgetCents, spentCents) ?? 0);

    /// <summary>One row per category with a weekly budget, in the order given (category sort order).</summary>
    public static IReadOnlyList<WeekBudgetRowDto> CategoryRows(WeekWindow week, IEnumerable<WeekBudgetCategory> budgets, IReadOnlyList<WeekOccurrence> occurrences, IReadOnlyList<WeekExpense> expenses) =>
        budgets.Select(b => Row(b.Id, b.Name, b.Color, b.Icon, b.BudgetCents, SpentCents(week, b.Id, occurrences, expenses))).ToList();

    /// <summary>The overall target row (<c>categoryId = null</c>, spent = everything), or null without a target.</summary>
    public static WeekBudgetRowDto? OverallRow(WeekWindow week, long? targetCents, IReadOnlyList<WeekOccurrence> occurrences, IReadOnlyList<WeekExpense> expenses) =>
        targetCents is { } target ? Row(null, OverallName, OverallColor, OverallIcon, target, SpentCents(week, null, occurrences, expenses)) : null;

    /// <summary>Totals over the category rows only (like v2 monthly budgets), so the two numbers compare like with like.</summary>
    public static (long Budget, long Spent) Totals(IEnumerable<WeekBudgetRowDto> categoryRows)
    {
        long budget = 0, spent = 0;
        foreach (var row in categoryRows)
        {
            budget += row.BudgetCents;
            spent += row.SpentCents;
        }

        return (budget, spent);
    }

    /// <summary>
    /// Over/under history: the viewed week and the <c>weeks − 1</c> weeks before it, oldest first, judged against today's budgets
    /// (budgets are not versioned). Per week: with an overall target, <c>budget = target</c> and <c>spent</c> = everything; without one,
    /// <c>budget = Σ category weekly budgets</c> and <c>spent</c> = Σ spent in those categories. <c>variance = spent − budget</c>.
    /// Empty when there is neither a target nor a category budget.
    /// </summary>
    public static IReadOnlyList<WeekBudgetHistoryPoint> History(
        WeekWindow week,
        long? targetCents,
        IReadOnlyList<WeekBudgetCategory> budgets,
        IReadOnlyList<WeekOccurrence> occurrences,
        IReadOnlyList<WeekExpense> expenses,
        int weeks = HistoryWeeks)
    {
        if (targetCents is null && budgets.Count == 0)
        {
            return [];
        }

        var result = new List<WeekBudgetHistoryPoint>(weeks);
        for (var i = weeks - 1; i >= 0; i--)
        {
            var w = week.Shift(-i);
            long budget, spent;
            if (targetCents is { } target)
            {
                budget = target;
                spent = SpentCents(w, null, occurrences, expenses);
            }
            else
            {
                budget = budgets.Sum(b => b.BudgetCents);
                spent = budgets.Sum(b => SpentCents(w, b.Id, occurrences, expenses));
            }

            result.Add(new WeekBudgetHistoryPoint(w.Start, budget, spent, spent - budget));
        }

        return result;
    }
}
