using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>Push subscriptions, notification preferences, the reminder log and the week-summary log.</summary>
public sealed class PushRepository(NpgsqlDataSource db)
{
    private const string SubscriptionColumns = "id, user_id, endpoint, p256dh, auth, user_agent, created_at, last_used_at, failures";
    private const string PrefsColumns = "user_id, reminders_enabled, days_before, hour_local, activity_enabled, week_summary_enabled, updated_at";

    // ---------------------------------------------------------------------------------------------
    // Subscriptions
    // ---------------------------------------------------------------------------------------------

    /// <summary>Upsert by endpoint. A re-subscribed endpoint is re-owned by the caller and its failure count reset.</summary>
    public async Task<Guid> UpsertSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, string? userAgent, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(Cmd(
            """
            INSERT INTO push_subscriptions (id, user_id, endpoint, p256dh, auth, user_agent, created_at, failures)
            VALUES (@Id, @UserId, @Endpoint, @P256dh, @Auth, @UserAgent, @Now, 0)
            ON CONFLICT (endpoint) DO UPDATE
                SET user_id = EXCLUDED.user_id, p256dh = EXCLUDED.p256dh, auth = EXCLUDED.auth,
                    user_agent = COALESCE(EXCLUDED.user_agent, push_subscriptions.user_agent), failures = 0
            RETURNING id
            """,
            new { Id = Guid.CreateVersion7(), UserId = userId, Endpoint = endpoint, P256dh = p256dh, Auth = auth, UserAgent = userAgent, Now = now }, ct));
    }

    /// <summary>Removes the caller's subscription matched by endpoint and/or id. False when nothing of theirs matched.</summary>
    public async Task<bool> DeleteSubscriptionAsync(Guid userId, string? endpoint, Guid? id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            """
            DELETE FROM push_subscriptions
            WHERE user_id = @UserId
              AND (@Endpoint::text IS NULL OR endpoint = @Endpoint)
              AND (@Id::uuid IS NULL OR id = @Id)
              AND (@Endpoint::text IS NOT NULL OR @Id::uuid IS NOT NULL)
            """,
            new { UserId = userId, Endpoint = endpoint, Id = id }, ct));
        return rows > 0;
    }

    public async Task DeleteSubscriptionAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd("DELETE FROM push_subscriptions WHERE id = @Id", new { Id = id }, ct));
    }

    public async Task<IReadOnlyList<PushSubscriptionRow>> SubscriptionsForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PushSubscriptionRow>(Cmd(
            $"SELECT {SubscriptionColumns} FROM push_subscriptions WHERE user_id = @UserId ORDER BY created_at, id",
            new { UserId = userId }, ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PushSubscriptionRow>> SubscriptionsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PushSubscriptionRow>(Cmd(
            $"SELECT {SubscriptionColumns} FROM push_subscriptions WHERE user_id = ANY(@UserIds) ORDER BY user_id, created_at, id",
            new { UserIds = userIds.ToArray() }, ct));
        return rows.ToList();
    }

    public async Task MarkDeliveredAsync(Guid id, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE push_subscriptions SET last_used_at = @Now, failures = 0 WHERE id = @Id",
            new { Id = id, Now = now }, ct));
    }

    /// <summary>Increments the failure counter; returns the new value.</summary>
    public async Task<int> RecordFailureAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(Cmd(
            "UPDATE push_subscriptions SET failures = failures + 1 WHERE id = @Id RETURNING failures",
            new { Id = id }, ct)) ?? 0;
    }

    public async Task<int> DeleteDeadSubscriptionsAsync(int maxFailures, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(Cmd(
            "DELETE FROM push_subscriptions WHERE failures >= @Max",
            new { Max = maxFailures }, ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Preferences
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns the saved prefs, creating the defaults row on first access.</summary>
    public async Task<NotificationPrefsRow> GetOrCreatePrefsAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "INSERT INTO notification_prefs (user_id, updated_at) VALUES (@UserId, @Now) ON CONFLICT (user_id) DO NOTHING",
            new { UserId = userId, Now = now }, ct));
        return await conn.QuerySingleAsync<NotificationPrefsRow>(Cmd(
            $"SELECT {PrefsColumns} FROM notification_prefs WHERE user_id = @UserId",
            new { UserId = userId }, ct));
    }

    public async Task<NotificationPrefsRow> UpsertPrefsAsync(Guid userId, bool remindersEnabled, short[] daysBefore, int hourLocal, bool activityEnabled, bool weekSummaryEnabled, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<NotificationPrefsRow>(Cmd(
            $"""
            INSERT INTO notification_prefs (user_id, reminders_enabled, days_before, hour_local, activity_enabled, week_summary_enabled, updated_at)
            VALUES (@UserId, @RemindersEnabled, @DaysBefore, @HourLocal, @ActivityEnabled, @WeekSummaryEnabled, @Now)
            ON CONFLICT (user_id) DO UPDATE
                SET reminders_enabled = EXCLUDED.reminders_enabled, days_before = EXCLUDED.days_before,
                    hour_local = EXCLUDED.hour_local, activity_enabled = EXCLUDED.activity_enabled,
                    week_summary_enabled = EXCLUDED.week_summary_enabled, updated_at = EXCLUDED.updated_at
            RETURNING {PrefsColumns}
            """,
            new { UserId = userId, RemindersEnabled = remindersEnabled, DaysBefore = daysBefore, HourLocal = (short)hourLocal, ActivityEnabled = activityEnabled, WeekSummaryEnabled = weekSummaryEnabled, Now = now }, ct));
    }

    /// <summary>Members of a household who want partner-activity pushes (no prefs row = defaults = enabled), minus <paramref name="exceptUserId"/>.</summary>
    public async Task<IReadOnlyList<Guid>> ActivityRecipientsAsync(Guid householdId, Guid? exceptUserId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(Cmd(
            """
            SELECT m.user_id
            FROM household_members m
            LEFT JOIN notification_prefs p ON p.user_id = m.user_id
            WHERE m.household_id = @HouseholdId
              AND (@Except::uuid IS NULL OR m.user_id <> @Except)
              AND COALESCE(p.activity_enabled, true)
            """,
            new { HouseholdId = householdId, Except = exceptUserId }, ct));
        return rows.ToList();
    }

    /// <summary>Users with at least one push subscription and a household, with their reminder / week-summary prefs (defaults when unsaved).</summary>
    public async Task<IReadOnlyList<ReminderUserRow>> ReminderUsersAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReminderUserRow>(Cmd(
            """
            SELECT m.user_id, m.household_id, h.timezone, h.currency, h.week_start, h.payday_weekday,
                   COALESCE(p.reminders_enabled, true) AS reminders_enabled,
                   COALESCE(p.days_before, '{1}'::smallint[]) AS days_before,
                   COALESCE(p.hour_local, 9) AS hour_local,
                   COALESCE(p.week_summary_enabled, true) AS week_summary_enabled
            FROM household_members m
            JOIN households h ON h.id = m.household_id
            LEFT JOIN notification_prefs p ON p.user_id = m.user_id
            WHERE EXISTS (SELECT 1 FROM push_subscriptions s WHERE s.user_id = m.user_id)
            ORDER BY m.household_id, m.user_id
            """, null, ct));
        return rows.ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Reminder log
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlySet<(Guid ItemId, DateOnly DueDate)>> SentRemindersAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid ItemId, DateOnly DueDate)>(Cmd(
            "SELECT recurring_item_id, due_date FROM reminder_log WHERE user_id = @UserId AND due_date BETWEEN @From AND @To",
            new { UserId = userId, From = from, To = to }, ct));
        return rows.ToHashSet();
    }

    /// <summary>Returns false when a reminder for this (item, due date, user) was already logged.</summary>
    public async Task<bool> TryLogReminderAsync(Guid itemId, DateOnly dueDate, Guid userId, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO reminder_log (recurring_item_id, due_date, user_id, sent_at)
            VALUES (@ItemId, @DueDate, @UserId, @Now)
            ON CONFLICT DO NOTHING
            """,
            new { ItemId = itemId, DueDate = dueDate, UserId = userId, Now = now }, ct));
        return rows > 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Week summary log (v4)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns false when this (user, week start, kind) summary was already sent.</summary>
    public async Task<bool> TryLogWeekSummaryAsync(Guid userId, DateOnly weekStart, WeekSummaryKind kind, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO week_summary_log (user_id, week_start, kind, sent_at)
            VALUES (@UserId, @WeekStart, @Kind, @Now)
            ON CONFLICT DO NOTHING
            """,
            new { UserId = userId, WeekStart = weekStart, Kind = kind.ToDb(), Now = now }, ct));
        return rows > 0;
    }
}
