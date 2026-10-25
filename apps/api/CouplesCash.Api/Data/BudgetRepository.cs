using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary><c>budgets</c>: one row per (category, period). v2 rows are <c>monthly</c>; v4 adds <c>weekly</c> rows beside them.</summary>
public sealed class BudgetRepository(NpgsqlDataSource db)
{
    private const string Columns = "household_id, category_id, period, amount_cents, updated_at";

    /// <summary>The household's budgets of one period (default monthly, as every v2 caller expects).</summary>
    public async Task<IReadOnlyList<BudgetRow>> ListAsync(Guid householdId, CancellationToken ct, BudgetPeriod period = BudgetPeriod.Monthly)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BudgetRow>(Cmd(
            $"SELECT {Columns} FROM budgets WHERE household_id = @HouseholdId AND period = @Period",
            new { HouseholdId = householdId, Period = period.ToDb() }, ct));
        return rows.ToList();
    }

    /// <summary>Sets a category's budget for the period. The category must belong to the household (checked by the caller).</summary>
    public Task<BudgetRow> UpsertAsync(NpgsqlConnection conn, Guid householdId, Guid categoryId, BudgetPeriod period, long amountCents, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<BudgetRow>(Cmd(
            $"""
            INSERT INTO budgets (household_id, category_id, period, amount_cents, updated_at)
            VALUES (@HouseholdId, @CategoryId, @Period, @AmountCents, @Now)
            ON CONFLICT (household_id, category_id, period) DO UPDATE SET amount_cents = EXCLUDED.amount_cents, updated_at = EXCLUDED.updated_at
            RETURNING {Columns}
            """,
            new { HouseholdId = householdId, CategoryId = categoryId, Period = period.ToDb(), AmountCents = amountCents, Now = now }, ct));

    public async Task<bool> DeleteAsync(NpgsqlConnection conn, Guid householdId, Guid categoryId, BudgetPeriod period, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            "DELETE FROM budgets WHERE household_id = @HouseholdId AND category_id = @CategoryId AND period = @Period",
            new { HouseholdId = householdId, CategoryId = categoryId, Period = period.ToDb() }, ct));
        return rows > 0;
    }
}
