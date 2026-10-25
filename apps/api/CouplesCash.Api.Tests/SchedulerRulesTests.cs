using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class SchedulerRulesTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly Guid Rent = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Netflix = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Manual = Guid.Parse("00000000-0000-7000-8000-000000000003");

    private static readonly AutopayCandidate[] Items =
    [
        new(Rent, AutoPay: true, new Recurrence(Frequency.Monthly, D(2026, 1, 31), null)),
        new(Netflix, AutoPay: true, new Recurrence(Frequency.Monthly, D(2026, 1, 10), null)),
        new(Manual, AutoPay: false, new Recurrence(Frequency.Monthly, D(2026, 1, 28), null)),
    ];

    [Fact]
    public void AutopayDueToday_selects_only_autopay_items_due_today_with_clamping()
    {
        var due = SchedulerRules.AutopayDueToday(Items, D(2026, 2, 28));

        Assert.Equal([new AutopayDue(Rent, D(2026, 2, 28))], due);
    }

    [Fact]
    public void AutopayDueToday_is_empty_on_other_days()
    {
        Assert.Empty(SchedulerRules.AutopayDueToday(Items, D(2026, 2, 11)));
    }

    [Fact]
    public void Autopay_lookback_catches_a_missed_day_but_skips_handled_occurrences()
    {
        var handled = new HashSet<(Guid, DateOnly)> { (Netflix, D(2026, 2, 10)) };

        var due = SchedulerRules.AutopayDue(Items, D(2026, 2, 12), lookbackDays: 3, handled);

        Assert.Empty(due); // Netflix on the 10th is within lookback but already paid/undone
        var fresh = SchedulerRules.AutopayDue(Items, D(2026, 2, 12), lookbackDays: 3);
        Assert.Equal([new AutopayDue(Netflix, D(2026, 2, 10))], fresh);
    }

    [Fact]
    public void Autopay_ignores_ended_items()
    {
        var ended = new AutopayCandidate(Rent, true, new Recurrence(Frequency.Monthly, D(2025, 1, 1), D(2025, 12, 31)));

        Assert.Empty(SchedulerRules.AutopayDueToday([ended], D(2026, 1, 1)));
    }

    [Theory]
    [InlineData(true, 9, 9, true)]
    [InlineData(true, 9, 8, false)]
    [InlineData(true, 9, 10, false)]
    [InlineData(false, 9, 9, false)]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 23, 23, true)]
    public void ShouldSendReminderNow_matches_the_local_hour(bool enabled, int hourLocal, int nowHour, bool expected)
    {
        var nowLocal = new DateTime(2026, 3, 1, nowHour, 37, 0, DateTimeKind.Unspecified);

        Assert.Equal(expected, SchedulerRules.ShouldSendReminderNow(enabled, hourLocal, nowLocal));
    }

    [Fact]
    public void ReminderDates_adds_each_days_before_deduplicated_and_sorted()
    {
        var dates = SchedulerRules.ReminderDates(D(2026, 3, 1), [3, 0, 1, 1, -5]);

        Assert.Equal([D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 4)], dates);
    }

    [Fact]
    public void DueReminders_filters_to_the_dates_and_skips_already_sent()
    {
        var unpaid = new[]
        {
            new ReminderCandidate(Rent, "Rent", 185_000, D(2026, 3, 2)),
            new ReminderCandidate(Netflix, "Netflix", 1_599, D(2026, 3, 2)),
            new ReminderCandidate(Manual, "Water", 4_000, D(2026, 3, 5)),
        };
        var sent = new HashSet<(Guid, DateOnly)> { (Rent, D(2026, 3, 2)) };

        var due = SchedulerRules.DueReminders(unpaid, [D(2026, 3, 2)], sent);

        Assert.Equal([new ReminderCandidate(Netflix, "Netflix", 1_599, D(2026, 3, 2))], due);
    }

    [Fact]
    public void Reminder_body_reads_naturally()
    {
        var today = D(2026, 11, 3);
        Assert.Equal("Electric is due tomorrow · $142.50", SchedulerRules.ReminderBody("Electric", 14_250, "USD", D(2026, 11, 4), today));
        Assert.Equal("Rent is due today · $1,850.00", SchedulerRules.ReminderBody("Rent", 185_000, "USD", today, today));
        Assert.Equal("Water is due in 3 days (Fri Nov 6) · €40.00", SchedulerRules.ReminderBody("Water", 4_000, "EUR", D(2026, 11, 6), today));
    }

    [Fact]
    public void Autopay_body_and_calendar_link()
    {
        Assert.Equal("Rent auto-paid · $1,850.00", SchedulerRules.AutopayBody("Rent", 185_000, "USD"));
        Assert.Equal("/calendar?m=2026-11&d=2026-11-06", SchedulerRules.CalendarLink(D(2026, 11, 6)));
    }

    [Fact]
    public void ToLocal_converts_with_iana_zone_and_falls_back_to_utc()
    {
        var utc = new DateTime(2026, 7, 1, 3, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 6, 30, 22, 30, 0), SchedulerRules.ToLocal(utc, "America/Chicago")); // CDT = UTC−5
        Assert.Equal(utc, SchedulerRules.ToLocal(utc, "Mars/Olympus_Mons"));
        Assert.Equal(utc, SchedulerRules.ToLocal(utc, null));
    }

    // ---------------------------------------------------------------------------------------------
    // v4: week summaries
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 10, 25, 1, WeekSummaryKind.Ahead, "2026-10-26")] // Sunday before a Monday start → ahead, keyed on Monday
    [InlineData(2026, 10, 23, 1, WeekSummaryKind.Left, "2026-10-19")]  // Friday = 5th day of a Monday week → left, keyed on this Monday
    [InlineData(2026, 10, 24, 0, WeekSummaryKind.Ahead, "2026-10-25")] // Saturday before a Sunday start
    [InlineData(2026, 10, 22, 0, WeekSummaryKind.Left, "2026-10-18")]  // Thursday = 5th day of a Sunday week
    public void WeekSummaryDueToday_picks_the_day_before_week_start_and_the_fifth_day(int y, int m, int d, int weekStart, WeekSummaryKind kind, string keyedOn)
    {
        var due = SchedulerRules.WeekSummaryDueToday(D(y, m, d), weekStart);

        Assert.NotNull(due);
        Assert.Equal(kind, due.Value.Kind);
        Assert.Equal(DateOnly.Parse(keyedOn), due.Value.WeekStart);
    }

    [Theory]
    [InlineData(2026, 10, 19, 1)] // Monday
    [InlineData(2026, 10, 20, 1)] // Tuesday
    [InlineData(2026, 10, 21, 1)] // Wednesday
    [InlineData(2026, 10, 22, 1)] // Thursday
    [InlineData(2026, 10, 24, 1)] // Saturday
    [InlineData(2026, 10, 23, 0)] // Friday with a Sunday start
    [InlineData(2026, 10, 25, 0)] // Sunday itself with a Sunday start (the "ahead" went out on Saturday)
    public void WeekSummaryDueToday_is_null_on_other_days(int y, int m, int d, int weekStart)
    {
        Assert.Null(SchedulerRules.WeekSummaryDueToday(D(y, m, d), weekStart));
    }

    [Fact]
    public void Week_summary_fires_exactly_twice_per_week()
    {
        foreach (var weekStart in new[] { 0, 1 })
        {
            var week = WeekEngine.WeekBounds(D(2026, 10, 22), weekStart);
            var kinds = Enumerable.Range(0, 7).Select(i => SchedulerRules.WeekSummaryDueToday(week.Start.AddDays(i), weekStart)).Where(d => d is not null).Select(d => d!.Value.Kind).ToList();

            Assert.Equal([WeekSummaryKind.Left, WeekSummaryKind.Ahead], kinds);
        }
    }

    [Fact]
    public void Week_summary_bodies_read_naturally()
    {
        Assert.Equal("Week ahead: $60.00 planned, $2,390.00 expected left", SchedulerRules.WeekAheadBody(6_000, 239_000, "USD"));
        Assert.Equal("Week ahead: $60.00 planned", SchedulerRules.WeekAheadBody(6_000, null, "USD"));
        Assert.Equal("You have $2,390.00 left this week · budget $4.20 over", SchedulerRules.WeekLeftBody(239_000, 420, "USD"));
        Assert.Equal("You have $2,390.00 left this week · budget $12.00 under", SchedulerRules.WeekLeftBody(239_000, -1_200, "USD"));
        Assert.Equal("You have €5.00 left this week · budget on target", SchedulerRules.WeekLeftBody(500, 0, "EUR"));
        Assert.Equal("You have $5.00 left this week", SchedulerRules.WeekLeftBody(500, null, "USD"));
        Assert.Equal("Weekly check-in · budget $1.00 over", SchedulerRules.WeekLeftBody(null, 100, "USD"));
        Assert.Equal("week-ahead", SchedulerRules.WeekSummaryTag(WeekSummaryKind.Ahead));
        Assert.Equal("week-left", SchedulerRules.WeekSummaryTag(WeekSummaryKind.Left));
        Assert.Equal("/week", SchedulerRules.WeekLink);
    }

    [Fact]
    public void LocalMidnightUtc_converts_the_start_of_a_local_day()
    {
        Assert.Equal(new DateTime(2026, 7, 1, 5, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(D(2026, 7, 1), "America/Chicago")); // CDT = UTC−5
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(D(2026, 7, 1), null));
    }
}
