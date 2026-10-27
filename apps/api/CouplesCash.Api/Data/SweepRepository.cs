using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>v7 <c>sweeps</c>: one savings sweep per (household, pay week). Every statement is household-scoped.</summary>
public sealed class SweepRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, household_id, week_start, planned_cents, left_cents, actual_cents, done_at, done_by, transaction_id, auto_blocked, created_at";

    public async Task<SweepRow?> GetAsync(Guid householdId, DateOnly weekStart, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, weekStart, ct);
    }

    public Task<SweepRow?> GetAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"SELECT {Columns} FROM sweeps WHERE household_id = @HouseholdId AND week_start = @WeekStart",
            new { HouseholdId = householdId, WeekStart = weekStart }, ct));

    /// <summary>Row lock for a tick / untick or the auto-detection so two of them cannot interleave.</summary>
    public Task<SweepRow?> LockAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"SELECT {Columns} FROM sweeps WHERE household_id = @HouseholdId AND week_start = @WeekStart FOR UPDATE",
            new { HouseholdId = householdId, WeekStart = weekStart }, ct));

    /// <summary>Bank rows already attached to a sweep (they are never claimed twice).</summary>
    public async Task<IReadOnlySet<Guid>> AttachedTransactionIdsAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Guid>(Cmd(
            "SELECT transaction_id FROM sweeps WHERE household_id = @HouseholdId AND transaction_id IS NOT NULL",
            new { HouseholdId = householdId }, ct));
        return rows.ToHashSet();
    }

    /// <summary>Freezes a pay week's sweep. Idempotent: returns null when the week already has one.</summary>
    public Task<SweepRow?> InsertAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, long plannedCents, long leftCents, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"""
            INSERT INTO sweeps (id, household_id, week_start, planned_cents, left_cents, created_at)
            VALUES (@Id, @HouseholdId, @WeekStart, @Planned, @Left, @Now)
            ON CONFLICT (household_id, week_start) DO NOTHING
            RETURNING {Columns}
            """,
            new { Id = Guid.CreateVersion7(), HouseholdId = householdId, WeekStart = weekStart, Planned = plannedCents, Left = leftCents, Now = now }, ct));

    /// <summary>
    /// The sweep day's amount follows "Left on Thursday" until the move is done (a bill or one-off can change during the day). The bank row
    /// that moves it is detected in the same transaction that imports it, so the amount it is matched against is never the one after it.
    /// </summary>
    public Task<SweepRow?> FollowLeftAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, long plannedCents, long leftCents, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"""
            UPDATE sweeps SET planned_cents = @Planned, left_cents = @Left
            WHERE household_id = @HouseholdId AND week_start = @WeekStart AND done_at IS NULL
              AND (planned_cents <> @Planned OR left_cents <> @Left)
            RETURNING {Columns}
            """,
            new { HouseholdId = householdId, WeekStart = weekStart, Planned = plannedCents, Left = leftCents }, ct));

    /// <summary>Done (manual: <paramref name="doneBy"/> set; auto: null) with the amount moved and, when known, the bank row.</summary>
    public Task<SweepRow?> MarkDoneAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, long actualCents, Guid? doneBy, Guid? transactionId, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"""
            UPDATE sweeps SET actual_cents = @Actual, done_at = @Now, done_by = @DoneBy, transaction_id = COALESCE(@TransactionId, transaction_id)
            WHERE household_id = @HouseholdId AND week_start = @WeekStart
            RETURNING {Columns}
            """,
            new { HouseholdId = householdId, WeekStart = weekStart, Actual = actualCents, DoneBy = doneBy, TransactionId = transactionId, Now = now }, ct));

    /// <summary>A manual tick happened first; the bank row that later shows up is attached (the tick and its amount are kept).</summary>
    public Task<SweepRow?> AttachTransactionAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, Guid transactionId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"""
            UPDATE sweeps SET transaction_id = @TransactionId
            WHERE household_id = @HouseholdId AND week_start = @WeekStart AND transaction_id IS NULL
            RETURNING {Columns}
            """,
            new { HouseholdId = householdId, WeekStart = weekStart, TransactionId = transactionId }, ct));

    /// <summary>Untick: back to not done (the transaction link is dropped too) and blocked from auto-detection so the Undo sticks.</summary>
    public Task<SweepRow?> UndoAsync(NpgsqlConnection conn, Guid householdId, DateOnly weekStart, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SweepRow>(Cmd(
            $"""
            UPDATE sweeps SET actual_cents = NULL, done_at = NULL, done_by = NULL, transaction_id = NULL, auto_blocked = true
            WHERE household_id = @HouseholdId AND week_start = @WeekStart
            RETURNING {Columns}
            """,
            new { HouseholdId = householdId, WeekStart = weekStart }, ct));
}
