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
}
