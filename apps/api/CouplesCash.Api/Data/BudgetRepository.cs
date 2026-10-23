using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class BudgetRepository(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<BudgetRow>> ListAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BudgetRow>(Cmd(
            "SELECT household_id, category_id, amount_cents, updated_at FROM budgets WHERE household_id = @HouseholdId",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>Sets the monthly budget of a category. The category must belong to the household (checked by the caller).</summary>
    public Task<BudgetRow> UpsertAsync(NpgsqlConnection conn, Guid householdId, Guid categoryId, long amountCents, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<BudgetRow>(Cmd(
            """
            INSERT INTO budgets (household_id, category_id, amount_cents, updated_at)
            VALUES (@HouseholdId, @CategoryId, @AmountCents, @Now)
            ON CONFLICT (household_id, category_id) DO UPDATE SET amount_cents = EXCLUDED.amount_cents, updated_at = EXCLUDED.updated_at
            RETURNING household_id, category_id, amount_cents, updated_at
            """,
            new { HouseholdId = householdId, CategoryId = categoryId, AmountCents = amountCents, Now = now }, ct));

    public async Task<bool> DeleteAsync(NpgsqlConnection conn, Guid householdId, Guid categoryId, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            "DELETE FROM budgets WHERE household_id = @HouseholdId AND category_id = @CategoryId",
            new { HouseholdId = householdId, CategoryId = categoryId }, ct));
        return rows > 0;
    }
}
