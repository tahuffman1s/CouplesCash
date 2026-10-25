using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class WeekEngineTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly Guid Rent = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Oil = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Salary = Guid.Parse("00000000-0000-7000-8000-000000000003");
    private static readonly Guid Netflix = Guid.Parse("00000000-0000-7000-8000-000000000004");
    private static readonly Guid Housing = Guid.Parse("00000000-0000-7000-8000-000000000020");
    private static readonly Guid Transport = Guid.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly Guid Groceries = Guid.Parse("00000000-0000-7000-8000-000000000022");

    // Monday-start week 2026-10-19 … 2026-10-25, today = Thursday 2026-10-22.
    private static readonly WeekWindow Week = new(D(2026, 10, 19), D(2026, 10, 25));
    private static readonly DateOnly Today = D(2026, 10, 22);

    private static readonly WeekOccurrence[] Occurrences =
    [
        new(Rent, Kind.Bill, 185_000, D(2026, 10, 20), PaidCents: 185_000, Housing),      // Tue, cleared at plan
        new(Oil, Kind.Bill, 6_000, D(2026, 10, 22), PaidCents: 5_420, Transport),         // Thu, cleared under plan
        new(Netflix, Kind.Subscription, 1_599, D(2026, 10, 24), PaidCents: null, null),   // Sat, planned (uncategorised)
        new(Salary, Kind.Income, 250_000, D(2026, 10, 23), PaidCents: null),              // Fri, planned income
        new(Rent, Kind.Bill, 4_000, D(2026, 10, 21), PaidCents: null, Housing),           // Wed, unpaid → overdue in-week
        new(Rent, Kind.Bill, 9_900, D(2026, 10, 12), PaidCents: null, Housing),           // last week, still unpaid → overdue before
        new(Rent, Kind.Bill, 1_000, D(2026, 10, 26), PaidCents: null, Housing),           // next week
    ];

    private static readonly WeekExpense[] Expenses =
    [
        new(D(2026, 10, 19), 1_249, Groceries),
        new(D(2026, 10, 22), 450, null),
        new(D(2026, 10, 18), 7_777, Groceries), // previous week
    ];

    // ---------------------------------------------------------------------------------------------
    // Week bounds
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 10, 22, 1, "2026-10-19", "2026-10-25")] // Thursday, Monday start
    [InlineData(2026, 10, 19, 1, "2026-10-19", "2026-10-25")] // Monday itself
    [InlineData(2026, 10, 25, 1, "2026-10-19", "2026-10-25")] // Sunday belongs to the Monday week
    [InlineData(2026, 10, 22, 0, "2026-10-18", "2026-10-24")] // Thursday, Sunday start
    [InlineData(2026, 10, 18, 0, "2026-10-18", "2026-10-24")] // Sunday itself
    [InlineData(2026, 10, 25, 0, "2026-10-25", "2026-10-31")] // Sunday starts the next week
    [InlineData(2026, 1, 1, 1, "2025-12-29", "2026-01-04")]   // crosses the year
    public void WeekBounds_follow_the_household_week_start(int y, int m, int d, int weekStart, string start, string end)
    {
        var week = WeekEngine.WeekBounds(D(y, m, d), weekStart);

        Assert.Equal(DateOnly.Parse(start), week.Start);
        Assert.Equal(DateOnly.Parse(end), week.End);
        Assert.Equal(6, week.End.DayNumber - week.Start.DayNumber);
    }

    [Fact]
    public void WeekBounds_rejects_other_week_starts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeekEngine.WeekBounds(Today, 2));
    }

    [Fact]
    public void Current_past_future_are_exclusive()
    {
        Assert.True(WeekEngine.IsCurrent(Week, Today));
        Assert.False(WeekEngine.IsPast(Week, Today));
        Assert.False(WeekEngine.IsFuture(Week, Today));

        Assert.True(WeekEngine.IsPast(Week, D(2026, 10, 26)));
        Assert.False(WeekEngine.IsCurrent(Week, D(2026, 10, 26)));

        Assert.True(WeekEngine.IsFuture(Week, D(2026, 10, 18)));
        Assert.False(WeekEngine.IsCurrent(Week, D(2026, 10, 18)));
        Assert.True(WeekEngine.IsCurrent(Week, Week.End)); // last day still current
    }

    // ---------------------------------------------------------------------------------------------
    // Status and variance
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-21", false, OccurrenceStatus.Overdue)]  // yesterday, unpaid
    [InlineData("2026-10-22", false, OccurrenceStatus.Planned)]  // due today is not overdue yet
    [InlineData("2026-10-24", false, OccurrenceStatus.Planned)]
    [InlineData("2026-10-21", true, OccurrenceStatus.Cleared)]   // paid wins over the date
    [InlineData("2026-10-24", true, OccurrenceStatus.Cleared)]
    public void Status_is_cleared_overdue_or_planned(string due, bool paid, OccurrenceStatus expected)
    {
        Assert.Equal(expected, WeekEngine.Status(DateOnly.Parse(due), paid, Today));
    }

    [Theory]
    [InlineData(null, 6000, null)]
    [InlineData(5420L, 6000, -580L)]  // cost less than planned
    [InlineData(6420L, 6000, 420L)]   // cost more → positive
    [InlineData(6000L, 6000, 0L)]
    public void Variance_is_actual_minus_planned(long? paid, long planned, long? expected)
    {
        Assert.Equal(expected, WeekEngine.Variance(paid, planned));
    }

    [Fact]
    public void IsOverdueBefore_only_flags_unpaid_items_before_the_week_and_before_today()
    {
        Assert.True(WeekEngine.IsOverdueBefore(D(2026, 10, 12), paid: false, Week.Start, Today));
        Assert.False(WeekEngine.IsOverdueBefore(D(2026, 10, 12), paid: true, Week.Start, Today));
        Assert.False(WeekEngine.IsOverdueBefore(D(2026, 10, 21), paid: false, Week.Start, Today)); // in-week: a day row, status overdue
        // Future week viewed on Oct 22: an unpaid item due Oct 24 is upcoming, not overdue.
        Assert.False(WeekEngine.IsOverdueBefore(D(2026, 10, 24), paid: false, D(2026, 10, 26), Today));
    }

    // ---------------------------------------------------------------------------------------------
    // Totals
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Totals_split_planned_actual_and_remaining_inside_the_week_only()
    {
        var t = WeekEngine.Totals(Week, Occurrences, Expenses);

        Assert.Equal(250_000, t.IncomeExpectedCents);
        Assert.Equal(0, t.IncomeReceivedCents);
        Assert.Equal(185_000 + 6_000 + 1_599 + 4_000, t.PlannedOutCents);
        Assert.Equal(185_000 + 5_420, t.PaidOutCents);          // actuals
        Assert.Equal(1_599 + 4_000, t.RemainingOutCents);       // unpaid plans
        Assert.Equal(1_249 + 450, t.UnplannedCents);            // Oct 18 expense excluded
    }

    [Fact]
    public void Totals_of_an_empty_week_are_zero()
    {
        var t = WeekEngine.Totals(Week.Shift(5), Occurrences, Expenses);

        Assert.Equal(default, t);
    }

    // ---------------------------------------------------------------------------------------------
    // Left at end of week
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Current_week_left_is_spendable_plus_income_minus_unpaid_outflow_for_the_rest_of_the_week()
    {
        // Rest of week = Oct 22 … 25: Netflix 1,599 unpaid (Sat), Salary 250,000 (Fri). The paid Oil change and the
        // Wednesday overdue 4,000 are not deducted again; the Oct 26 rent is next week.
        var left = WeekEngine.LeftAtEnd(Week, Today, spendableNowCents: 100_000, startingBalanceCents: null, Occurrences, Expenses);

        Assert.Equal(100_000 + 250_000 - 1_599, left);
    }

    [Fact]
    public void Planning_an_unpaid_one_off_reduces_left_by_its_planned_amount()
    {
        var before = WeekEngine.LeftAtEnd(Week, Today, 100_000, null, Occurrences, Expenses);
        var withOneOff = Occurrences.Append(new WeekOccurrence(Oil, Kind.Bill, 6_000, D(2026, 10, 23), null, Transport)).ToList();

        var after = WeekEngine.LeftAtEnd(Week, Today, 100_000, null, withOneOff, Expenses);

        Assert.Equal(before - 6_000, after);
    }

    [Fact]
    public void Past_week_left_is_null_without_a_starting_balance()
    {
        Assert.Null(WeekEngine.LeftAtEnd(Week, D(2026, 10, 30), 100_000, null, Occurrences, Expenses));
    }

    [Fact]
    public void Past_week_left_uses_actuals_from_the_starting_balance()
    {
        var left = WeekEngine.LeftAtEnd(Week, D(2026, 10, 30), spendableNowCents: 999, startingBalanceCents: 300_000, Occurrences, Expenses);

        // starting + income received (0) − paid out (185,000 + 5,420) − unplanned (1,249 + 450); the unpaid Netflix is not deducted.
        Assert.Equal(300_000 - 185_000 - 5_420 - 1_249 - 450, left);
    }

    [Fact]
    public void Future_week_left_projects_from_today_through_that_weeks_end()
    {
        var next = Week.Shift(1); // Oct 26 … Nov 1
        var left = WeekEngine.LeftAtEnd(next, Today, 100_000, null, Occurrences, Expenses);

        // Everything unpaid from Oct 22 to Nov 1: Salary +250,000, Netflix −1,599, Oct 26 rent −1,000.
        Assert.Equal(100_000 + 250_000 - 1_599 - 1_000, left);
        Assert.True(WeekEngine.IsFuture(next, Today));
    }

    [Fact]
    public void ProjectedLeft_ignores_paid_items_and_items_outside_the_range()
    {
        Assert.Equal(100_000, WeekEngine.ProjectedLeft(100_000, D(2026, 10, 19), D(2026, 10, 20), Occurrences)); // only the paid rent
        Assert.Equal(100_000 - 4_000, WeekEngine.ProjectedLeft(100_000, D(2026, 10, 21), D(2026, 10, 21), Occurrences));
    }

    // ---------------------------------------------------------------------------------------------
    // Weekly budgets
    // ---------------------------------------------------------------------------------------------

    private static readonly WeekBudgetCategory[] Budgets =
    [
        new(Housing, "Housing", "#f97316", "home", 200_000),
        new(Transport, "Transportation", "#3b82f6", "car", 5_000),
        new(Groceries, "Groceries", "#22c55e", "shopping-cart", 10_000),
    ];

    [Fact]
    public void Category_spent_is_expenses_plus_paid_bills_in_the_week_by_category()
    {
        Assert.Equal(185_000, WeekEngine.SpentCents(Week, Housing, Occurrences, Expenses));   // paid rent; unpaid 4,000 and last week's 9,900 excluded
        Assert.Equal(5_420, WeekEngine.SpentCents(Week, Transport, Occurrences, Expenses));   // actual, not the 6,000 plan
        Assert.Equal(1_249, WeekEngine.SpentCents(Week, Groceries, Occurrences, Expenses));   // Oct 18 expense excluded
    }

    [Fact]
    public void Overall_spent_counts_everything_including_uncategorised()
    {
        Assert.Equal(185_000 + 5_420 + 1_249 + 450, WeekEngine.SpentCents(Week, null, Occurrences, Expenses));
    }

    [Fact]
    public void Income_never_counts_as_spent()
    {
        var paidSalary = new[] { new WeekOccurrence(Salary, Kind.Income, 250_000, D(2026, 10, 23), 250_000, Groceries) };

        Assert.Equal(0, WeekEngine.SpentCents(Week, null, paidSalary, []));
        Assert.Equal(0, WeekEngine.SpentCents(Week, Groceries, paidSalary, []));
    }

    [Fact]
    public void Category_rows_carry_variance_sign_and_percent()
    {
        var rows = WeekEngine.CategoryRows(Week, Budgets, Occurrences, Expenses);

        Assert.Equal(3, rows.Count);
        var transport = rows.Single(r => r.CategoryId == Transport);
        Assert.Equal(5_000, transport.BudgetCents);
        Assert.Equal(5_420, transport.SpentCents);
        Assert.Equal(420, transport.VarianceCents);   // over → positive
        Assert.Equal(108, transport.Percent);

        var groceries = rows.Single(r => r.CategoryId == Groceries);
        Assert.Equal(-8_751, groceries.VarianceCents); // under → negative
        Assert.Equal(12, groceries.Percent);

        var housing = rows.Single(r => r.CategoryId == Housing);
        Assert.Equal(-15_000, housing.VarianceCents);
        Assert.Equal("Housing", housing.Name);
    }

    [Fact]
    public void Overall_row_is_null_without_a_target_and_covers_everything_with_one()
    {
        Assert.Null(WeekEngine.OverallRow(Week, null, Occurrences, Expenses));

        var row = WeekEngine.OverallRow(Week, 200_000, Occurrences, Expenses)!;
        Assert.Null(row.CategoryId);
        Assert.Equal(WeekEngine.OverallName, row.Name);
        Assert.Equal(192_119, row.SpentCents);
        Assert.Equal(-7_881, row.VarianceCents);
        Assert.Equal(96, row.Percent);
    }

    [Fact]
    public void Totals_over_category_rows_only()
    {
        var rows = WeekEngine.CategoryRows(Week, Budgets, Occurrences, Expenses);

        var (budget, spent) = WeekEngine.Totals(rows);

        Assert.Equal(215_000, budget);
        Assert.Equal(185_000 + 5_420 + 1_249, spent);
    }

    [Fact]
    public void Zero_budget_percent_follows_budget_math()
    {
        Assert.Equal(100, WeekEngine.Row(null, "x", "#000000", "wallet", 0, 1).Percent);
        Assert.Equal(0, WeekEngine.Row(null, "x", "#000000", "wallet", 0, 0).Percent);
    }

    // ---------------------------------------------------------------------------------------------
    // History
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void History_has_eight_weeks_oldest_first_ending_with_the_viewed_week()
    {
        var history = WeekEngine.History(Week, 200_000, Budgets, Occurrences, Expenses);

        Assert.Equal(8, history.Count);
        Assert.Equal(Week.Start.AddDays(-49), history[0].WeekStart);
        Assert.Equal(Week.Start, history[^1].WeekStart);
        Assert.Equal(history.OrderBy(h => h.WeekStart).Select(h => h.WeekStart), history.Select(h => h.WeekStart));

        var current = history[^1];
        Assert.Equal(200_000, current.BudgetCents);
        Assert.Equal(192_119, current.SpentCents);
        Assert.Equal(-7_881, current.VarianceCents);

        var previous = history[^2];
        Assert.Equal(7_777, previous.SpentCents); // the Oct 18 expense; the unpaid Oct 12 rent is not spent
        Assert.Equal(7_777 - 200_000, previous.VarianceCents);
    }

    [Fact]
    public void History_without_a_target_uses_the_category_budgets()
    {
        var history = WeekEngine.History(Week, null, Budgets, Occurrences, Expenses);

        var current = history[^1];
        Assert.Equal(215_000, current.BudgetCents);
        Assert.Equal(185_000 + 5_420 + 1_249, current.SpentCents); // the uncategorised 450 is outside every budget
        Assert.Equal(current.SpentCents - 215_000, current.VarianceCents);
    }

    [Fact]
    public void History_is_empty_without_any_weekly_budget()
    {
        Assert.Empty(WeekEngine.History(Week, null, [], Occurrences, Expenses));
    }
}
