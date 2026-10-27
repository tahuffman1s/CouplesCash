using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Push;
using CouplesCash.Api.Services;
using Npgsql;

namespace CouplesCash.Api.Scheduler;

/// <summary>
/// The background job (PLAN-V2 "Background job"): every tick it
/// <list type="number">
/// <item>auto-marks <c>autoPay</c> occurrences due today (household time zone) — <c>auto = true</c>, <c>paid_by NULL</c>, an
/// <c>occurrence.autopaid</c> activity event and a push to members with activity pushes on;</item>
/// <item>sends due-date reminders during each user's local <c>hourLocal</c>, once per (item, due date, user) via <c>reminder_log</c>;</item>
/// <item>deletes expired WebAuthn challenges and dead push subscriptions;</item>
/// <item>(v3) syncs every enabled bank link whose last sync is older than <c>Sync__IntervalHours</c>, auto-matching
/// high-confidence recurring payments — see <see cref="BankSyncService"/>;</item>
/// <item>(v6) "rematch": re-scores every To-review row of a household at most every <c>Matcher__RematchMinutes</c> (30) — see
/// <see cref="MatchingService"/>; "found-bills": daily regular-payment detection per household — see <see cref="BillSuggestionService"/>;</item>
/// <item>(v7) freezes the week's savings sweep at payday 00:00 household time, pays planned transfers / ticks the sweep from the bank
/// feed (see <see cref="FridayService"/>) and sends the single payday push at each user's <c>hourLocal</c> on payday, once per (user,
/// pay week) via <c>week_summary_log</c> kind <c>payday</c> (the v4 week-ahead / week-left summaries are gone).</item>
/// </list>
/// Selection logic is in <see cref="SchedulerRules"/>; this class only does I/O. Every exception is caught and logged.
/// The logger category contains "Scheduler" so <c>make scheduler-logs</c> can filter for it.
/// </summary>
public sealed class SchedulerService(
    SchedulerOptions options,
    NpgsqlDataSource db,
    HouseholdRepository households,
    RecurringRepository items,
    OccurrenceService occurrences,
    PushRepository pushes,
    WebAuthnRepository webAuthn,
    ActivityService activity,
    PushNotifier notifier,
    BankSyncService bankSync,
    FridayService friday,
    MatchingService matching,
    BillSuggestionService bills,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Scheduler disabled (Scheduler__Enabled=false)");
            return;
        }

        // Wait until the app is serving (migrations have run) before the first tick.
        var started = new TaskCompletionSource();
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancel = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 5, 3600));
        logger.LogInformation("Scheduler started: tick every {Interval}s, autopay lookback {Lookback} day(s)", interval.TotalSeconds, options.AutopayLookbackDays);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One full pass. Exposed for tests and manual runs; each phase is isolated so one failure does not stop the others.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = time.GetUtcNow().UtcDateTime;
        await RunPhaseAsync("autopay", () => AutopayAsync(nowUtc, ct));
        await RunPhaseAsync("reminders", () => RemindersAsync(nowUtc, ct));
        await RunPhaseAsync("hygiene", () => HygieneAsync(nowUtc, ct));
        await RunPhaseAsync("sweep-freeze", () => friday.FreezeDueAsync(nowUtc, ct));
        await RunPhaseAsync("bank-sync", () => bankSync.SyncDueAsync(nowUtc, ct));
        await RunPhaseAsync("friday", () => friday.RunAllAsync(ct));
        await RunPhaseAsync("rematch", () => matching.SweepDueAsync(nowUtc, ct));
        await RunPhaseAsync("found-bills", () => bills.DetectDueAsync(nowUtc, ct));
        await RunPhaseAsync("payday-push", () => PaydayPushesAsync(nowUtc, ct));
    }

    private async Task RunPhaseAsync(string name, Func<Task> phase)
    {
        try
        {
            await phase();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler phase {Phase} failed", name);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 1. Autopay
    // ---------------------------------------------------------------------------------------------
    private async Task AutopayAsync(DateTime nowUtc, CancellationToken ct)
    {
        var autopayItems = await items.ListAutopayAsync(ct);
        if (autopayItems.Count == 0)
        {
            return;
        }

        var householdRows = (await households.ListAllAsync(ct)).ToDictionary(h => h.Id);
        var lookback = Math.Max(0, options.AutopayLookbackDays);

        foreach (var group in autopayItems.GroupBy(i => i.HouseholdId))
        {
            if (!householdRows.TryGetValue(group.Key, out var household))
            {
                continue;
            }

            var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(nowUtc, household.Timezone));
            var byId = group.ToDictionary(i => i.Id);
            var handled = await items.HandledOccurrencesAsync(byId.Keys.ToList(), today.AddDays(-lookback), today, ct);
            var due = SchedulerRules.AutopayDue(group.Select(i => new AutopayCandidate(i.Id, i.AutoPay, i.Rule)), today, lookback, handled);

            foreach (var occurrence in due)
            {
                var item = byId[occurrence.ItemId];
                await AutopayOneAsync(household, item, occurrence.DueDate, nowUtc, ct);
            }
        }
    }

    private async Task AutopayOneAsync(HouseholdRow household, RecurringItemRow item, DateOnly dueDate, DateTime nowUtc, CancellationToken ct)
    {
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var payment = await items.InsertAutoPaymentAsync(uow.Connection, item.Id, dueDate, item.AmountCents, nowUtc, ct);
        if (payment is null)
        {
            // Paid by hand, undone (skip row) or raced with another tick.
            return;
        }

        var summary = ActivitySummaries.OccurrenceAutopaid(item.Name, item.AmountCents, household.Currency);
        await activity.RecordAsync(uow.Connection, uow.Transaction, household.Id, null, "occurrence.autopaid", "occurrence", item.Id,
            summary, new { itemId = item.Id, dueDate, amountCents = item.AmountCents }, ct);
        uow.AfterCommit(() => notifier.ToHousehold(household.Id, null, new PushPayload(
            Title: "Auto-paid",
            Body: SchedulerRules.AutopayBody(item.Name, item.AmountCents, household.Currency),
            Url: SchedulerRules.CalendarLink(dueDate),
            Tag: $"autopay-{item.Id}-{dueDate:yyyyMMdd}")));
        await uow.CommitAsync(ct);

        logger.LogInformation("Scheduler auto-paid {Item} due {DueDate} for household {HouseholdId}", item.Name, dueDate, household.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Reminders
    // ---------------------------------------------------------------------------------------------
    private async Task RemindersAsync(DateTime nowUtc, CancellationToken ct)
    {
        var users = await pushes.ReminderUsersAsync(ct);
        var occurrenceCache = new Dictionary<(Guid HouseholdId, DateOnly From, DateOnly To), List<Contracts.OccurrenceDto>>();

        foreach (var user in users)
        {
            var nowLocal = SchedulerRules.ToLocal(nowUtc, user.Timezone);
            if (!SchedulerRules.ShouldSendReminderNow(user.RemindersEnabled, user.HourLocal, nowLocal))
            {
                continue;
            }

            var today = DateOnly.FromDateTime(nowLocal);
            var dates = SchedulerRules.ReminderDates(today, user.DaysBefore.Select(d => (int)d));
            if (dates.Count == 0)
            {
                continue;
            }

            var (from, to) = (dates[0], dates[^1]);
            var key = (user.HouseholdId, from, to);
            if (!occurrenceCache.TryGetValue(key, out var window))
            {
                window = await occurrences.ListAsync(user.HouseholdId, from, to, ct);
                occurrenceCache[key] = window;
            }

            var unpaid = window.Where(o => o.Kind.IsOutflow() && o.Paid is null)
                .Select(o => new ReminderCandidate(o.ItemId, o.Name, o.AmountCents, o.DueDate));
            var alreadySent = await pushes.SentRemindersAsync(user.UserId, from, to, ct);

            foreach (var reminder in SchedulerRules.DueReminders(unpaid, dates, alreadySent))
            {
                if (!await pushes.TryLogReminderAsync(reminder.ItemId, reminder.DueDate, user.UserId, nowUtc, ct))
                {
                    continue;
                }

                notifier.ToUser(user.UserId, new PushPayload(
                    Title: "Upcoming bill",
                    Body: SchedulerRules.ReminderBody(reminder.Name, reminder.AmountCents, user.Currency, reminder.DueDate, today),
                    Url: SchedulerRules.CalendarLink(reminder.DueDate),
                    Tag: $"reminder-{reminder.ItemId}-{reminder.DueDate:yyyyMMdd}"));
                logger.LogInformation("Scheduler reminder for {Item} due {DueDate} queued to user {UserId}", reminder.Name, reminder.DueDate, user.UserId);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Hygiene
    // ---------------------------------------------------------------------------------------------
    private async Task HygieneAsync(DateTime nowUtc, CancellationToken ct)
    {
        var challenges = await webAuthn.DeleteExpiredChallengesAsync(nowUtc, ct);
        var subscriptions = await pushes.DeleteDeadSubscriptionsAsync(PushDispatchService.MaxFailures, ct);
        if (challenges > 0 || subscriptions > 0)
        {
            logger.LogInformation("Scheduler hygiene: removed {Challenges} expired challenge(s), {Subscriptions} dead subscription(s)", challenges, subscriptions);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 5. The payday push (v7; replaces the v4 week-ahead / week-left summaries)
    // ---------------------------------------------------------------------------------------------
    private async Task PaydayPushesAsync(DateTime nowUtc, CancellationToken ct)
    {
        var users = await pushes.ReminderUsersAsync(ct);
        var bodyCache = new Dictionary<Guid, string?>();

        foreach (var user in users)
        {
            var nowLocal = SchedulerRules.ToLocal(nowUtc, user.Timezone);
            var today = DateOnly.FromDateTime(nowLocal);
            if (!SchedulerRules.ShouldSendReminderNow(user.WeekSummaryEnabled, user.HourLocal, nowLocal))
            {
                continue;
            }

            // The sweep day (the last day of the pay week): what is left moves to savings today.
            if (SchedulerRules.IsSweepDay(today, user.PaydayWeekday))
            {
                var week = WeekEngine.WeekBounds(today, user.PaydayWeekday);
                if (await friday.SweepBodyAsync(user.HouseholdId, week, ct) is { } sweepBody
                    && await pushes.TryLogWeekSummaryAsync(user.UserId, week.Start, WeekSummaryKind.Sweep, nowUtc, ct))
                {
                    notifier.ToUser(user.UserId, new PushPayload("Savings", sweepBody, SchedulerRules.WeekLink, SchedulerRules.SweepTag));
                    logger.LogInformation("Scheduler sweep push for {Day} queued to user {UserId}", today, user.UserId);
                }

                continue;
            }

            if (!PlanEngine.IsPayday(today, user.PaydayWeekday))
            {
                continue;
            }

            if (!bodyCache.TryGetValue(user.HouseholdId, out var body))
            {
                body = await friday.PaydayBodyAsync(user.HouseholdId, ct);
                bodyCache[user.HouseholdId] = body;
            }

            if (body is null || !await pushes.TryLogWeekSummaryAsync(user.UserId, today, WeekSummaryKind.Payday, nowUtc, ct))
            {
                continue;
            }

            notifier.ToUser(user.UserId, new PushPayload("Payday", body, SchedulerRules.WeekLink, SchedulerRules.PaydayTag));
            logger.LogInformation("Scheduler payday push for {Payday} queued to user {UserId}", today, user.UserId);
        }
    }
}
