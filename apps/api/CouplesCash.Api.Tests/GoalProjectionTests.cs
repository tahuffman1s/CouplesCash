using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class GoalProjectionTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static GoalPoint P(int daysAgo, long value) => new(Now.AddDays(-daysAgo), value);

    [Fact]
    public void Pace_is_change_over_window_divided_by_months()
    {
        // +$600 over ~60.875 days (2 months) → $300/month
        var history = new[] { P(61, 100_000), P(30, 130_000), P(0, 160_000) };

        var pace = GoalProjection.MonthlyPace(history, Now);

        Assert.NotNull(pace);
        Assert.InRange(pace!.Value, 29_900, 30_100);
    }

    [Fact]
    public void Pace_ignores_points_older_than_90_days()
    {
        var history = new[] { P(200, 0), P(80, 100_000), P(20, 120_000) };

        var pace = GoalProjection.MonthlyPace(history, Now);

        // (120000 − 100000) over 60 days ≈ 1.97 months → ≈ 10 145/month, not the huge jump from day 200.
        Assert.InRange(pace!.Value, 10_000, 10_300);
    }

    [Fact]
    public void Pace_is_null_with_fewer_than_two_points_or_under_seven_days()
    {
        Assert.Null(GoalProjection.MonthlyPace([P(10, 500)], Now));
        Assert.Null(GoalProjection.MonthlyPace([P(5, 500), P(0, 900)], Now));
        Assert.Null(GoalProjection.MonthlyPace([], Now));
    }

    [Fact]
    public void Projected_date_is_remaining_divided_by_pace()
    {
        var history = new[] { P(61, 100_000), P(0, 160_000) }; // ≈ $300/month
        var forecast = GoalProjection.Project(history, targetCents: 220_000, currentCents: 160_000, targetDate: null, Now);

        // $600 remaining at $300/month ≈ 2 months ≈ 61 days
        Assert.NotNull(forecast.ProjectedDate);
        var days = forecast.ProjectedDate!.Value.DayNumber - DateOnly.FromDateTime(Now).DayNumber;
        Assert.InRange(days, 59, 63);
        Assert.Null(forecast.MonthlyNeededCents);
    }

    [Fact]
    public void Projected_date_is_null_when_pace_is_not_positive()
    {
        var falling = new[] { P(61, 160_000), P(0, 100_000) };
        var forecast = GoalProjection.Project(falling, 220_000, 100_000, null, Now);

        Assert.True(forecast.MonthlyPaceCents < 0);
        Assert.Null(forecast.ProjectedDate);
    }

    [Fact]
    public void Reached_goal_has_no_projection_or_need()
    {
        var forecast = GoalProjection.Project([P(40, 0), P(0, 5_000)], 5_000, 5_000, DateOnly.FromDateTime(Now).AddMonths(3), Now);

        Assert.Null(forecast.ProjectedDate);
        Assert.Null(forecast.MonthlyNeededCents);
        Assert.NotNull(forecast.MonthlyPaceCents);
    }

    [Fact]
    public void Monthly_needed_divides_remaining_by_months_until_target()
    {
        var target = DateOnly.FromDateTime(Now).AddDays(61); // ≈ 2 months
        var forecast = GoalProjection.Project([], 300_000, 100_000, target, Now);

        Assert.InRange(forecast.MonthlyNeededCents!.Value, 99_000, 101_000);
        Assert.Null(forecast.MonthlyPaceCents);
    }

    [Fact]
    public void Monthly_needed_is_everything_when_target_date_has_passed()
    {
        var forecast = GoalProjection.Project([], 300_000, 100_000, DateOnly.FromDateTime(Now).AddDays(-3), Now);

        Assert.Equal(200_000, forecast.MonthlyNeededCents);
    }

    [Fact]
    public void Monthly_needed_rounds_up_to_whole_cents()
    {
        var target = DateOnly.FromDateTime(Now).AddDays(91); // ≈ 2.99 months
        var forecast = GoalProjection.Project([], 1_000, 0, target, Now);

        Assert.Equal(335, forecast.MonthlyNeededCents); // 1000 / 2.9897 = 334.48 → 335
    }
}
