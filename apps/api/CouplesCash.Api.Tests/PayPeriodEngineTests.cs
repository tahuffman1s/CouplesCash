using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class PayPeriodEngineTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Weekly_counts_seven_days_from_anchor()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Weekly, D(2026, 1, 2), D(2026, 1, 15)); // anchor Fri Jan 2

        Assert.Equal(D(2026, 1, 9), w.Start);
        Assert.Equal(D(2026, 1, 15), w.End);
        Assert.Equal(D(2026, 1, 16), w.NextPayday);
        Assert.Equal(1, w.DaysLeft(D(2026, 1, 15)));
    }

    [Fact]
    public void Weekly_on_payday_starts_a_new_period()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Weekly, D(2026, 1, 2), D(2026, 1, 16));

        Assert.Equal(D(2026, 1, 16), w.Start);
        Assert.Equal(D(2026, 1, 22), w.End);
        Assert.Equal(7, w.DaysLeft(D(2026, 1, 16)));
    }

    [Fact]
    public void Biweekly_counts_fourteen_days_and_crosses_month_end()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Biweekly, D(2026, 1, 9), D(2026, 2, 3));

        Assert.Equal(D(2026, 1, 23), w.Start);
        Assert.Equal(D(2026, 2, 5), w.End);
        Assert.Equal(D(2026, 2, 6), w.NextPayday);
    }

    [Fact]
    public void Fixed_step_anchor_in_the_future_projects_backwards()
    {
        // Anchor is the first payday of a new job next month; the current period is still computable.
        var w = PayPeriodEngine.Resolve(PayPeriod.Biweekly, D(2026, 3, 13), D(2026, 2, 20));

        Assert.Equal(D(2026, 2, 13), w.Start);
        Assert.Equal(D(2026, 2, 26), w.End);
        Assert.Equal(D(2026, 2, 27), w.NextPayday);
    }

    [Fact]
    public void Monthly_uses_anchor_day_and_clamps_to_short_months()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Monthly, D(2025, 1, 31), D(2026, 2, 10));

        Assert.Equal(D(2026, 1, 31), w.Start);
        Assert.Equal(D(2026, 2, 27), w.End);
        Assert.Equal(D(2026, 2, 28), w.NextPayday); // Feb 2026 has 28 days
    }

    [Fact]
    public void Monthly_leap_february_clamps_to_29()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Monthly, D(2023, 5, 30), D(2024, 2, 15));

        Assert.Equal(D(2024, 1, 30), w.Start);
        Assert.Equal(D(2024, 2, 29), w.NextPayday);
    }

    [Fact]
    public void Monthly_on_payday_day_starts_new_period()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Monthly, D(2026, 1, 15), D(2026, 3, 15));

        Assert.Equal(D(2026, 3, 15), w.Start);
        Assert.Equal(D(2026, 4, 14), w.End);
    }

    [Fact]
    public void Monthly_first_of_month_covers_the_whole_month()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Monthly, D(2026, 1, 1), D(2026, 2, 28));

        Assert.Equal(D(2026, 2, 1), w.Start);
        Assert.Equal(D(2026, 2, 28), w.End);
        Assert.Equal(D(2026, 3, 1), w.NextPayday);
    }

    [Theory]
    [InlineData(1, 1, 16)]
    [InlineData(16, 1, 16)]
    [InlineData(5, 5, 20)]
    [InlineData(20, 5, 20)]
    [InlineData(15, 15, 30)]
    [InlineData(31, 16, 31)]
    public void Bimonthly_days_follow_the_plus_15_rule(int anchorDay, int first, int second)
    {
        Assert.Equal([first, second], PayPeriodEngine.BimonthlyDays(anchorDay));
    }

    [Fact]
    public void Bimonthly_1st_and_16th()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 1), D(2026, 1, 20));

        Assert.Equal(D(2026, 1, 16), w.Start);
        Assert.Equal(D(2026, 1, 31), w.End);
        Assert.Equal(D(2026, 2, 1), w.NextPayday);

        var early = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 1), D(2026, 2, 3));
        Assert.Equal(D(2026, 2, 1), early.Start);
        Assert.Equal(D(2026, 2, 15), early.End);
    }

    [Fact]
    public void Bimonthly_20th_and_5th()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 20), D(2026, 1, 3));

        Assert.Equal(D(2025, 12, 20), w.Start);
        Assert.Equal(D(2026, 1, 4), w.End);
        Assert.Equal(D(2026, 1, 5), w.NextPayday);

        var later = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 20), D(2026, 1, 25));
        Assert.Equal(D(2026, 1, 20), later.Start);
        Assert.Equal(D(2026, 2, 4), later.End);
    }

    [Fact]
    public void Bimonthly_15th_and_30th_clamps_in_february()
    {
        var w = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 15), D(2026, 2, 20));

        Assert.Equal(D(2026, 2, 15), w.Start);
        Assert.Equal(D(2026, 2, 27), w.End);
        Assert.Equal(D(2026, 2, 28), w.NextPayday);

        var after = PayPeriodEngine.Resolve(PayPeriod.Bimonthly, D(2026, 1, 15), D(2026, 2, 28));
        Assert.Equal(D(2026, 2, 28), after.Start);
        Assert.Equal(D(2026, 3, 14), after.End);
    }

    [Fact]
    public void Paydays_in_month_are_distinct_and_sorted()
    {
        var days = PayPeriodEngine.PaydaysInMonth(2026, 2, [30, 15]).ToList();

        Assert.Equal([D(2026, 2, 15), D(2026, 2, 28)], days);
    }

    [Theory]
    [InlineData(PayPeriod.Weekly)]
    [InlineData(PayPeriod.Biweekly)]
    [InlineData(PayPeriod.Monthly)]
    [InlineData(PayPeriod.Bimonthly)]
    public void Every_date_in_a_year_falls_in_exactly_one_contiguous_period(PayPeriod period)
    {
        var anchor = D(2025, 11, 28);
        var previous = PayPeriodEngine.Resolve(period, anchor, D(2025, 12, 31));
        for (var date = D(2026, 1, 1); date <= D(2026, 12, 31); date = date.AddDays(1))
        {
            var w = PayPeriodEngine.Resolve(period, anchor, date);
            Assert.True(w.Start <= date && date <= w.End, $"{period}: {date} outside [{w.Start}, {w.End}]");
            Assert.Equal(w.End.AddDays(1), w.NextPayday);
            if (w.Start != previous.Start)
            {
                Assert.Equal(previous.NextPayday, w.Start);
            }

            previous = w;
        }
    }
}
