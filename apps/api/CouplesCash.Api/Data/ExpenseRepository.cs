using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed record ExpenseFields(string Name, long AmountCents, Guid? CategoryId, Guid? AccountId, DateOnly SpentOn, string? Note, bool Deducted);

public sealed class ExpenseRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, household_id, name, amount_cents, category_id, account_id, spent_on, note, deducted, created_by, created_at, updated_at";

    /// <summary>Expenses dated within [from, to], newest spent_on first (then newest created).</summary>
    public async Task<IReadOnlyList<ExpenseRow>> ListAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ExpenseRow>(Cmd(
            $"""
            SELECT {Columns} FROM expenses
            WHERE household_id = @HouseholdId AND spent_on BETWEEN @From AND @To
            ORDER BY spent_on DESC, created_at DESC, id DESC
            """,
            new { HouseholdId = householdId, From = from, To = to }, ct));
        return rows.ToList();
    }

    public async Task<ExpenseRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, id, ct);
    }

    public Task<ExpenseRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExpenseRow>(Cmd(
            $"SELECT {Columns} FROM expenses WHERE id = @Id AND household_id = @HouseholdId FOR UPDATE",
            new { Id = id, HouseholdId = householdId }, ct));

    public Task<ExpenseRow> CreateAsync(NpgsqlConnection conn, Guid householdId, Guid userId, ExpenseFields f, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<ExpenseRow>(Cmd(
            $"""
            INSERT INTO expenses (id, household_id, name, amount_cents, category_id, account_id, spent_on, note, deducted, created_by, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Name, @AmountCents, @CategoryId, @AccountId, @SpentOn, @Note, @Deducted, @UserId, @Now, @Now)
            RETURNING {Columns}
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                HouseholdId = householdId,
                f.Name,
                f.AmountCents,
                f.CategoryId,
                f.AccountId,
                f.SpentOn,
                f.Note,
                f.Deducted,
                UserId = userId,
                Now = now,
            }, ct));

    public Task<ExpenseRow?> UpdateAsync(NpgsqlConnection conn, Guid householdId, Guid id, ExpenseFields f, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExpenseRow>(Cmd(
            $"""
            UPDATE expenses
            SET name = @Name, amount_cents = @AmountCents, category_id = @CategoryId, account_id = @AccountId,
                spent_on = @SpentOn, note = @Note, deducted = @Deducted, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            RETURNING {Columns}
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                f.Name,
                f.AmountCents,
                f.CategoryId,
                f.AccountId,
                f.SpentOn,
                f.Note,
                f.Deducted,
                Now = now,
            }, ct));

    public Task<ExpenseRow?> DeleteAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExpenseRow>(Cmd(
            $"DELETE FROM expenses WHERE id = @Id AND household_id = @HouseholdId RETURNING {Columns}",
            new { Id = id, HouseholdId = householdId }, ct));
}
