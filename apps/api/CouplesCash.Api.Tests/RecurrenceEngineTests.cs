using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class RecurrenceEngineTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static Recurrence Monthly(DateOnly anchor, DateOnly? end = null) => new(Frequency.Monthly, anchor, end);

    [Fact]
    public void Monthly_anchor_on_31st_clamps_to_month_end_and_springs_back()
    {
        var rule = Monthly(D(2024, 1, 31));

        var dates = RecurrenceEngine.Expand(rule, D(2024, 1, 1), D(2024, 5, 31)).ToList();

        Assert.Equal([D(2024, 1, 31), D(2024, 2, 29), D(2024, 3, 31), D(2024, 4, 30), D(2024, 5, 31)], dates);
    }

    [Fact]
    public void Monthly_anchor_on_31st_in_non_leap_year_gives_feb_28()
    {
        var rule = Monthly(D(2023, 1, 31));

        var dates = RecurrenceEngine.Expand(rule, D(2023, 2, 1), D(2023, 3, 31)).ToList();

        Assert.Equal([D(2023, 2, 28), D(2023, 3, 31)], dates);
    }

    [Fact]
    public void Monthly_clamping_is_relative_to_anchor_not_previous_occurrence()
    {
        // Starting in a 30-day month must not permanently lose the 31st.
        var rule = Monthly(D(2024, 1, 31));

        var dates = RecurrenceEngine.Expand(rule, D(2024, 4, 1), D(2024, 7, 31)).ToList();

        Assert.Equal([D(2024, 4, 30), D(2024, 5, 31), D(2024, 6, 30), D(2024, 7, 31)], dates);
    }

    [Fact]
    public void Quarterly_steps_three_months_with_clamping()
    {
        var rule = new Recurrence(Frequency.Quarterly, D(2023, 11, 30), null);

        var dates = RecurrenceEngine.Expand(rule, D(2024, 1, 1), D(2024, 12, 31)).ToList();

        Assert.Equal([D(2024, 2, 29), D(2024, 5, 30), D(2024, 8, 30), D(2024, 11, 30)], dates);
    }

    [Fact]
    public void Yearly_on_leap_day_falls_back_to_feb_28()
    {
        var rule = new Recurrence(Frequency.Yearly, D(2024, 2, 29), null);

        var dates = RecurrenceEngine.Expand(rule, D(2025, 1, 1), D(2028, 12, 31)).ToList();

        Assert.Equal([D(2025, 2, 28), D(2026, 2, 28), D(2027, 2, 28), D(2028, 2, 29)], dates);
    }

    [Fact]
    public void Biweekly_steps_14_days_from_anchor()
    {
        var rule = new Recurrence(Frequency.Biweekly, D(2024, 1, 5), null);

        var dates = RecurrenceEngine.Expand(rule, D(2024, 2, 1), D(2024, 3, 15)).ToList();

        Assert.Equal([D(2024, 2, 2), D(2024, 2, 16), D(2024, 3, 1), D(2024, 3, 15)], dates);
    }

    [Fact]
    public void Weekly_steps_7_days_and_skips_history_efficiently()
    {
        var rule = new Recurrence(Frequency.Weekly, D(2000, 1, 3), null);

        var dates = RecurrenceEngine.Expand(rule, D(2024, 6, 1), D(2024, 6, 30)).ToList();

        Assert.Equal([D(2024, 6, 3), D(2024, 6, 10), D(2024, 6, 17), D(2024, 6, 24)], dates);
        Assert.All(dates, d => Assert.Equal(DayOfWeek.Monday, d.DayOfWeek));
    }

    [Fact]
    public void End_date_cuts_the_series_inclusive()
    {
        var rule = Monthly(D(2024, 1, 15), D(2024, 3, 15));

        var dates = RecurrenceEngine.Expand(rule, D(2024, 1, 1), D(2024, 12, 31)).ToList();

        Assert.Equal([D(2024, 1, 15), D(2024, 2, 15), D(2024, 3, 15)], dates);
    }

    [Fact]
    public void Once_yields_exactly_the_anchor_when_in_range()
    {
        var rule = new Recurrence(Frequency.Once, D(2024, 7, 4), null);

        Assert.Equal([D(2024, 7, 4)], RecurrenceEngine.Expand(rule, D(2024, 7, 1), D(2024, 7, 31)));
        Assert.Empty(RecurrenceEngine.Expand(rule, D(2024, 8, 1), D(2024, 8, 31)));
        Assert.Empty(RecurrenceEngine.Expand(rule, D(2024, 6, 1), D(2024, 6, 30)));
    }

    [Fact]
    public void Range_boundaries_are_inclusive()
    {
        var rule = Monthly(D(2024, 1, 10));

        Assert.Equal([D(2024, 3, 10)], RecurrenceEngine.Expand(rule, D(2024, 3, 10), D(2024, 3, 10)));
        Assert.Equal([D(2024, 2, 10), D(2024, 3, 10)], RecurrenceEngine.Expand(rule, D(2024, 2, 10), D(2024, 3, 10)));
        Assert.Empty(RecurrenceEngine.Expand(rule, D(2024, 3, 11), D(2024, 4, 9)));
    }

    [Fact]
    public void Nothing_before_the_anchor_and_nothing_for_inverted_ranges()
    {
        var rule = Monthly(D(2024, 6, 1));

        Assert.Empty(RecurrenceEngine.Expand(rule, D(2024, 1, 1), D(2024, 5, 31)));
        Assert.Empty(RecurrenceEngine.Expand(rule, D(2024, 8, 1), D(2024, 7, 1)));
    }

    [Theory]
    [InlineData(2024, 2, 29, true)]
    [InlineData(2024, 3, 31, true)]
    [InlineData(2024, 3, 30, false)]
    [InlineData(2024, 2, 28, false)]
    [InlineData(2023, 12, 31, false)]
    public void IsOccurrence_matches_the_expanded_series(int y, int m, int d, bool expected)
    {
        var rule = Monthly(D(2024, 1, 31));

        Assert.Equal(expected, RecurrenceEngine.IsOccurrence(rule, D(y, m, d)));
    }

    [Fact]
    public void NextDue_returns_today_when_due_today_and_null_after_end()
    {
        var rule = Monthly(D(2024, 1, 31), D(2024, 4, 30));

        Assert.Equal(D(2024, 2, 29), RecurrenceEngine.NextDue(rule, D(2024, 2, 29)));
        Assert.Equal(D(2024, 3, 31), RecurrenceEngine.NextDue(rule, D(2024, 3, 1)));
        Assert.Equal(D(2024, 4, 30), RecurrenceEngine.NextDue(rule, D(2024, 4, 1)));
        Assert.Null(RecurrenceEngine.NextDue(rule, D(2024, 5, 1)));
    }

    [Fact]
    public void NextDue_for_once_and_future_anchor()
    {
        Assert.Equal(D(2024, 7, 4), RecurrenceEngine.NextDue(new Recurrence(Frequency.Once, D(2024, 7, 4), null), D(2024, 1, 1)));
        Assert.Null(RecurrenceEngine.NextDue(new Recurrence(Frequency.Once, D(2024, 7, 4), null), D(2024, 7, 5)));
        Assert.Equal(D(2030, 1, 1), RecurrenceEngine.NextDue(new Recurrence(Frequency.Weekly, D(2030, 1, 1), null), D(2024, 1, 1)));
    }

    [Theory]
    [InlineData(Frequency.Weekly, 1000, 4333)]      // 1000 * 52 / 12 = 4333.33
    [InlineData(Frequency.Biweekly, 1000, 2167)]    // 1000 * 26 / 12 = 2166.67
    [InlineData(Frequency.Monthly, 1000, 1000)]
    [InlineData(Frequency.Quarterly, 1000, 333)]    // 333.33
    [InlineData(Frequency.Yearly, 1000, 83)]        // 83.33
    [InlineData(Frequency.Once, 1000, 0)]
    [InlineData(Frequency.Yearly, 150, 12)]         // 12.5 → half-even → 12
    [InlineData(Frequency.Yearly, 450, 38)]         // 37.5 → half-even → 38
    [InlineData(Frequency.Quarterly, 50, 17)]       // 16.67
    [InlineData(Frequency.Biweekly, 3, 6)]          // 6.5 → half-even → 6
    [InlineData(Frequency.Biweekly, 9, 20)]         // 19.5 → half-even → 20
    public void MonthlyEquivalent_uses_contract_factors_and_half_even_rounding(Frequency frequency, long amount, long expected)
    {
        Assert.Equal(expected, RecurrenceEngine.MonthlyEquivalent(amount, frequency));
    }
}
