using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class BankSyncRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    [Fact]
    public void Never_synced_links_are_due()
    {
        Assert.True(BankSyncRules.IsDue(null, null, Now, SixHours));
    }

    [Fact]
    public void Links_are_due_once_the_interval_has_passed()
    {
        Assert.True(BankSyncRules.IsDue(Now.AddHours(-6), null, Now, SixHours));
        Assert.False(BankSyncRules.IsDue(Now.AddHours(-5.9), null, Now, SixHours));
    }

    [Fact]
    public void Failed_attempts_back_off_at_the_same_cadence()
    {
        Assert.False(BankSyncRules.IsDue(Now.AddDays(-2), Now.AddMinutes(-1), Now, SixHours));
        Assert.True(BankSyncRules.IsDue(Now.AddDays(-2), Now.AddHours(-7), Now, SixHours));
    }

    [Fact]
    public void Zero_interval_means_every_tick()
    {
        Assert.True(BankSyncRules.IsDue(Now, Now, Now, TimeSpan.Zero));
    }

    [Fact]
    public void Cooldown_is_null_without_a_manual_sync_or_once_it_elapsed()
    {
        Assert.Null(BankSyncRules.CooldownRemaining(null, Now, Cooldown));
        Assert.Null(BankSyncRules.CooldownRemaining(Now.AddMinutes(-15), Now, Cooldown));
        Assert.Null(BankSyncRules.CooldownRemaining(Now.AddSeconds(-1), Now, TimeSpan.Zero));
    }

    [Fact]
    public void Cooldown_reports_the_time_left()
    {
        var remaining = BankSyncRules.CooldownRemaining(Now.AddMinutes(-5), Now, Cooldown);

        Assert.Equal(TimeSpan.FromMinutes(10), remaining);
        Assert.Equal(600, BankSyncRules.RetryAfterSeconds(remaining!.Value));
        Assert.Equal(1, BankSyncRules.RetryAfterSeconds(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(2, BankSyncRules.RetryAfterSeconds(TimeSpan.FromSeconds(1.2)));
    }

    [Fact]
    public void Fetch_window_is_90_days_first_then_30_and_runs_two_weeks_ahead()
    {
        var today = new DateOnly(2026, 10, 23);

        Assert.Equal((new DateOnly(2026, 7, 25), new DateOnly(2026, 11, 6)), BankSyncRules.FetchWindow(null, today));
        Assert.Equal((new DateOnly(2026, 9, 23), new DateOnly(2026, 11, 6)), BankSyncRules.FetchWindow(Now.AddHours(-6), today));
    }

    [Fact]
    public void A_regular_sync_is_one_request_inside_the_bridges_45_day_advisory()
    {
        var (start, end) = BankSyncRules.FetchWindow(Now.AddHours(-6), new DateOnly(2026, 10, 23));

        var request = Assert.Single(BankSyncRules.FetchRequests(start, end));
        Assert.Equal((start, end, true), request);
        Assert.True(end.DayNumber - start.DayNumber <= 44);
    }

    [Fact]
    public void The_first_sync_splits_its_history_into_requests_of_44_days_newest_first_with_pending_only_in_the_newest()
    {
        var (start, end) = BankSyncRules.FetchWindow(null, new DateOnly(2026, 10, 23));

        var requests = BankSyncRules.FetchRequests(start, end);

        Assert.Equal(
            [
                (new DateOnly(2026, 9, 23), new DateOnly(2026, 11, 6), true),
                (new DateOnly(2026, 8, 10), new DateOnly(2026, 9, 23), false),
                (new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 10), false),
            ],
            requests);
        Assert.All(requests, r => Assert.InRange(r.EndExclusive.DayNumber - r.Start.DayNumber, 1, BankSyncRules.MaxRequestDays));
    }

    [Theory]
    [InlineData(AccountType.Checking, 245050, 245050)]
    [InlineData(AccountType.Savings, -100, -100)]
    [InlineData(AccountType.Credit, -54000, 54000)]
    [InlineData(AccountType.Loan, -1250000, 1250000)]
    [InlineData(AccountType.Credit, 2500, -2500)] // overpaid card: a credit balance
    public void Liabilities_flip_sign_to_amount_owed(AccountType type, long bridge, long stored)
    {
        Assert.Equal(stored, BankSyncRules.ToAccountBalance(type, bridge));
    }
}
