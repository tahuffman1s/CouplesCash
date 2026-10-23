using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class CategoryRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, household_id, name, color, icon, sort_order, is_default";

    public async Task<IReadOnlyList<CategoryRow>> ListAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CategoryRow>(Cmd(
            $"SELECT {Columns} FROM categories WHERE household_id = @HouseholdId ORDER BY sort_order, name",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    public async Task<bool> ExistsAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(Cmd(
            "SELECT EXISTS (SELECT 1 FROM categories WHERE id = @Id AND household_id = @HouseholdId)",
            new { Id = id, HouseholdId = householdId }, ct));
    }

    public async Task<CategoryRow> CreateAsync(Guid householdId, string name, string color, string icon, int? sortOrder, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<CategoryRow>(Cmd(
            $"""
            INSERT INTO categories (id, household_id, name, color, icon, sort_order, is_default)
            VALUES (@Id, @HouseholdId, @Name, @Color, @Icon,
                    COALESCE(@SortOrder, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories WHERE household_id = @HouseholdId)),
                    false)
            RETURNING {Columns}
            """,
            new { Id = Guid.CreateVersion7(), HouseholdId = householdId, Name = name, Color = color, Icon = icon, SortOrder = sortOrder }, ct));
    }

    public async Task<CategoryRow?> UpdateAsync(Guid householdId, Guid id, string name, string color, string icon, int? sortOrder, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CategoryRow>(Cmd(
            $"""
            UPDATE categories
            SET name = @Name, color = @Color, icon = @Icon, sort_order = COALESCE(@SortOrder, sort_order)
            WHERE id = @Id AND household_id = @HouseholdId
            RETURNING {Columns}
            """,
            new { Id = id, HouseholdId = householdId, Name = name, Color = color, Icon = icon, SortOrder = sortOrder }, ct));
    }

    /// <summary>Hard delete; recurring_items.category_id is ON DELETE SET NULL.</summary>
    public async Task<bool> DeleteAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            "DELETE FROM categories WHERE id = @Id AND household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));
        return rows > 0;
    }
}
