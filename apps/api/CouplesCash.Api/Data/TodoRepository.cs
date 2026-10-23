using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class TodoRepository(NpgsqlDataSource db)
{
    private const string Select =
        """
        SELECT t.id, t.household_id, t.title, t.amount_cents, t.due_date, t.sort_order, t.done_at, t.done_by,
               u.display_name AS done_by_name, t.created_by, t.created_at, t.updated_at
        FROM todos t
        LEFT JOIN users u ON u.id = t.done_by
        """;

    /// <summary>Open items by sort order, then (optionally) items done within the last <paramref name="doneDays"/> days, newest first.</summary>
    public async Task<IReadOnlyList<TodoRow>> ListAsync(Guid householdId, bool includeDone, DateTime now, int doneDays, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TodoRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId
              AND (t.done_at IS NULL OR (@IncludeDone AND t.done_at >= @Since))
            ORDER BY (t.done_at IS NOT NULL), t.sort_order, t.created_at, t.done_at DESC
            """,
            new { HouseholdId = householdId, IncludeDone = includeDone, Since = now.AddDays(-doneDays) }, ct));

        var list = rows.ToList();
        return list.Where(t => t.DoneAt is null)
            .Concat(list.Where(t => t.DoneAt is not null).OrderByDescending(t => t.DoneAt))
            .ToList();
    }

    public async Task<TodoRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, id, ct);
    }

    public Task<TodoRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<TodoRow>(Cmd(
            $"{Select} WHERE t.id = @Id AND t.household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));

    public async Task<TodoRow> CreateAsync(NpgsqlConnection conn, Guid householdId, Guid userId, string title, long? amountCents, DateOnly? dueDate, DateTime now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO todos (id, household_id, title, amount_cents, due_date, sort_order, created_by, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Title, @AmountCents, @DueDate,
                    (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM todos WHERE household_id = @HouseholdId),
                    @UserId, @Now, @Now)
            """,
            new { Id = id, HouseholdId = householdId, Title = title, AmountCents = amountCents, DueDate = dueDate, UserId = userId, Now = now }, ct));
        return await GetAsync(conn, householdId, id, ct) ?? throw new InvalidOperationException("Todo vanished after insert");
    }

    public async Task<TodoRow?> UpdateAsync(NpgsqlConnection conn, Guid householdId, Guid id, string title, long? amountCents, DateOnly? dueDate, int? sortOrder, DateTime now, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            """
            UPDATE todos
            SET title = @Title, amount_cents = @AmountCents, due_date = @DueDate, sort_order = COALESCE(@SortOrder, sort_order), updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, Title = title, AmountCents = amountCents, DueDate = dueDate, SortOrder = sortOrder, Now = now }, ct));
        return rows == 0 ? null : await GetAsync(conn, householdId, id, ct);
    }

    /// <summary>Sets or clears done. Idempotent: marking a done item done again keeps the original who/when.</summary>
    public async Task<TodoRow?> SetDoneAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid? doneBy, bool done, DateTime now, CancellationToken ct)
    {
        var rows = done
            ? await conn.ExecuteAsync(Cmd(
                """
                UPDATE todos SET done_at = COALESCE(done_at, @Now), done_by = COALESCE(done_by, @DoneBy), updated_at = @Now
                WHERE id = @Id AND household_id = @HouseholdId
                """,
                new { Id = id, HouseholdId = householdId, Now = now, DoneBy = doneBy }, ct))
            : await conn.ExecuteAsync(Cmd(
                "UPDATE todos SET done_at = NULL, done_by = NULL, updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId",
                new { Id = id, HouseholdId = householdId, Now = now }, ct));
        return rows == 0 ? null : await GetAsync(conn, householdId, id, ct);
    }

    public Task<TodoRow?> DeleteAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<TodoRow>(Cmd(
            """
            DELETE FROM todos WHERE id = @Id AND household_id = @HouseholdId
            RETURNING id, household_id, title, amount_cents, due_date, sort_order, done_at, done_by, NULL::text AS done_by_name, created_by, created_at, updated_at
            """,
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>How many of the given ids belong to the household.</summary>
    public async Task<int> CountOwnedAsync(NpgsqlConnection conn, Guid householdId, Guid[] ids, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int>(Cmd(
            "SELECT count(*) FROM todos WHERE household_id = @HouseholdId AND id = ANY(@Ids)",
            new { HouseholdId = householdId, Ids = ids }, ct));

    /// <summary>Assigns sort_order = position for the given ids (household-scoped).</summary>
    public async Task ReorderAsync(NpgsqlConnection conn, Guid householdId, Guid[] ids, DateTime now, CancellationToken ct)
    {
        var rows = ids.Select((id, index) => new { Id = id, HouseholdId = householdId, SortOrder = index, Now = now });
        await conn.ExecuteAsync(Cmd(
            "UPDATE todos SET sort_order = @SortOrder, updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId",
            rows, ct));
    }
}
