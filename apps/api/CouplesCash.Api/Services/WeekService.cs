using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;

namespace CouplesCash.Api.Services;

/// <summary>
/// Composes <c>GET /v1/week</c> (and <c>GET /v1/budgets/week</c>, the scheduler's week summaries) from the household's
/// occurrences, expenses, balances and weekly budgets. All math is in <see cref="WeekEngine"/>; this class only loads
/// and shapes. "Today" is the household-local date.
/// </summary>
public sealed class WeekService(
    HouseholdRepository households,
    AccountRepository accounts,
    RecurringRepository items,
    OccurrenceService occurrences,
    ExpenseRepository expenses,
    BudgetRepository budgets,
    CategoryRepository categories,
    TimeProvider time)
{
    /// <summary>How far before the week an unpaid occurrence is still listed under <c>overdue</c>.</summary>
    public const int OverdueLookbackDays = 90;

    /// <summary>At most this many overdue rows are returned (oldest first).</summary>
    public const int OverdueMax = 50;

    /// <summary>The week containing <paramref name="start"/> (default: today in the household time zone). Null when the household row is missing.</summary>
    public async Task<WeekViewDto?> GetAsync(Guid householdId, DateOnly? start, CancellationToken ct)
    {
        var household = await households.GetRowAsync(householdId, ct);
        if (household is null)
        {
            return null;
        }

        var today = TodayFor(household);
        var week = WeekEngine.WeekBounds(start ?? today, household.WeekStart);
        return await BuildAsync(household, week, today, ct);
    }

    public static DateOnly TodayFor(HouseholdRow household, TimeProvider time) =>
        DateOnly.FromDateTime(SchedulerRules.ToLocal(time.Now(), household.Timezone));

    private DateOnly TodayFor(HouseholdRow household) => TodayFor(household, time);

    private async Task<WeekViewDto> BuildAsync(HouseholdRow household, WeekWindow week, DateOnly today, CancellationToken ct)
    {
        var householdId = household.Id;
        var historyStart = week.Shift(-(WeekEngine.HistoryWeeks - 1)).Start;

        // One occurrence range covers the overdue lookback, the 8-week budget history and (for current/future weeks) today → end.
        var from = Min(week.Start.AddDays(-OverdueLookbackDays), historyStart, today);
        var itemRows = await items.ListAsync(householdId, null, ct);
        var occurrenceDtos = await occurrences.ListAsync(householdId, itemRows, from, week.End, ct, today);
        var expenseRows = await expenses.ListAsync(householdId, historyStart, week.End, ct);
        var accountRows = await accounts.ListAsync(householdId, ct);
        var categoryRows = await categories.ListAsync(householdId, ct);
        var weeklyBudgets = await budgets.ListAsync(householdId, ct, BudgetPeriod.Weekly);

        var isPast = WeekEngine.IsPast(week, today);
        long? startingBalance = null;
        if (isPast)
        {
            startingBalance = await accounts.SpendableBalanceBeforeAsync(householdId, SchedulerRules.LocalMidnightUtc(week.Start, household.Timezone), ct);
        }

        var spendableNow = WeekEngine.Spendable(accountRows.Select(a => new SpendableAccount(a.Type, a.BalanceCents, a.AvailableCents)));
        var weekOccurrences = occurrenceDtos.Select(ToWeekOccurrence).ToList();
        var weekExpenses = expenseRows.Select(e => new WeekExpense(e.SpentOn, e.AmountCents, e.CategoryId)).ToList();

        var totals = WeekEngine.Totals(week, weekOccurrences, weekExpenses);
        var leftAtEnd = WeekEngine.LeftAtEnd(week, today, spendableNow, startingBalance, weekOccurrences, weekExpenses);

        var days = new List<WeekDayDto>(7);
        for (var date = week.Start; date <= week.End; date = date.AddDays(1))
        {
            var planned = occurrenceDtos.Where(o => o.DueDate == date).ToList();
            var dayExpenses = expenseRows.Where(e => e.SpentOn == date).OrderBy(e => e.CreatedAt).Select(e => e.ToDto()).ToList();
            days.Add(new WeekDayDto(
                date,
                planned,
                dayExpenses,
                planned.Where(o => o.Kind.IsOutflow()).Sum(o => o.AmountCents),
                planned.Where(o => !o.Kind.IsOutflow()).Sum(o => o.AmountCents),
                dayExpenses.Sum(e => e.AmountCents)));
        }

        var overdue = occurrenceDtos
            .Where(o => WeekEngine.IsOverdueBefore(o.DueDate, o.Paid is not null, week.Start, today))
            .OrderBy(o => o.DueDate)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(OverdueMax)
            .ToList();

        return new WeekViewDto(
            Start: week.Start,
            End: week.End,
            WeekStart: household.WeekStart,
            IsCurrent: WeekEngine.IsCurrent(week, today),
            IsPast: isPast,
            Today: today,
            SpendableNowCents: spendableNow,
            StartingBalanceCents: startingBalance,
            IncomeExpectedCents: totals.IncomeExpectedCents,
            IncomeReceivedCents: totals.IncomeReceivedCents,
            PlannedOutCents: totals.PlannedOutCents,
            PaidOutCents: totals.PaidOutCents,
            RemainingOutCents: totals.RemainingOutCents,
            UnplannedCents: totals.UnplannedCents,
            LeftAtEndCents: leftAtEnd,
            Projected: WeekEngine.IsFuture(week, today),
            Days: days,
            Overdue: overdue,
            Budget: BuildBudget(week, household.WeeklySpendCents, categoryRows, weeklyBudgets, weekOccurrences, weekExpenses),
            PendingOutCents: PendingOut(accountRows));
    }

    /// <summary>Weekly budget rows for the week: the overall target (if any) and every category with a weekly budget, in category order.</summary>
    public static WeekBudgetDto BuildBudget(
        WeekWindow week,
        long? targetCents,
        IReadOnlyList<CategoryRow> categoryRows,
        IReadOnlyList<BudgetRow> weeklyBudgets,
        IReadOnlyList<WeekOccurrence> weekOccurrences,
        IReadOnlyList<WeekExpense> weekExpenses)
    {
        var amounts = weeklyBudgets.ToDictionary(b => b.CategoryId, b => b.AmountCents);
        var budgeted = categoryRows
            .Where(c => amounts.ContainsKey(c.Id))
            .Select(c => new WeekBudgetCategory(c.Id, c.Name, c.Color, c.Icon, amounts[c.Id]))
            .ToList();

        var rows = WeekEngine.CategoryRows(week, budgeted, weekOccurrences, weekExpenses);
        var (totalBudget, totalSpent) = WeekEngine.Totals(rows);
        return new WeekBudgetDto(
            WeekEngine.OverallRow(week, targetCents, weekOccurrences, weekExpenses),
            rows,
            totalBudget,
            totalSpent,
            WeekEngine.History(week, targetCents, budgeted, weekOccurrences, weekExpenses));
    }

    /// <summary>v6 "Pending $X" beside Spendable: still-pending bank outflows on the same accounts Spendable counts (active checking + cash).</summary>
    public static long PendingOut(IEnumerable<AccountRow> accounts) =>
        accounts.Where(a => a.Type.IsSpendable() && a.ArchivedAt is null).Sum(a => a.PendingOutCents);

    public static WeekOccurrence ToWeekOccurrence(OccurrenceDto o) =>
        new(o.ItemId, o.Kind, o.AmountCents, o.DueDate, o.Paid?.AmountCents, o.CategoryId);

    private static DateOnly Min(DateOnly a, DateOnly b, DateOnly c)
    {
        var min = a < b ? a : b;
        return c < min ? c : min;
    }
}
