using CouplesCash.Api.Contracts;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class HouseholdRepository(NpgsqlDataSource db)
{
    private const string SelectHousehold = "SELECT id, name, created_at FROM households WHERE id = @Id";

    private const string SelectMembers =
        """
        SELECT u.id AS user_id, u.display_name, u.email, m.joined_at
        FROM household_members m
        JOIN users u ON u.id = m.user_id
        WHERE m.household_id = @Id
        ORDER BY m.joined_at, u.id
        """;

    /// <summary>Default categories, seeded into every new household in the same transaction.</summary>
    public static readonly IReadOnlyList<(string Name, string Color, string Icon)> DefaultCategories =
    [
        ("Housing", "#f97316", "home"),
        ("Utilities", "#eab308", "zap"),
        ("Insurance", "#06b6d4", "shield"),
        ("Transportation", "#3b82f6", "car"),
        ("Groceries", "#22c55e", "shopping-cart"),
        ("Subscriptions", "#a855f7", "repeat"),
        ("Debt", "#ef4444", "credit-card"),
        ("Health", "#ec4899", "heart-pulse"),
        ("Kids & Pets", "#14b8a6", "paw-print"),
        ("Fun", "#f43f5e", "party-popper"),
        ("Income", "#10b981", "banknote"),
        ("Other", "#6b7280", "ellipsis"),
    ];

    public async Task<Guid?> GetHouseholdIdForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Guid?>(Cmd(
            "SELECT household_id FROM household_members WHERE user_id = @UserId",
            new { UserId = userId }, ct));
    }

    public async Task<HouseholdDto?> GetAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await LoadAsync(conn, householdId, ct);
    }

    public async Task<HouseholdDto?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.QuerySingleOrDefaultAsync<Guid?>(Cmd(
            "SELECT household_id FROM household_members WHERE user_id = @UserId",
            new { UserId = userId }, ct));
        return id is null ? null : await LoadAsync(conn, id.Value, ct);
    }

    public async Task<int> MemberCountAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(Cmd(
            "SELECT count(*) FROM household_members WHERE household_id = @Id",
            new { Id = householdId }, ct));
    }

    /// <summary>Creates a household with its first member and the default categories, atomically.</summary>
    public async Task<HouseholdDto> CreateAsync(Guid userId, string name, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(Cmd(
            "INSERT INTO households (id, name, created_at) VALUES (@Id, @Name, @Now)",
            new { Id = id, Name = name, Now = now }, ct));
        await conn.ExecuteAsync(Cmd(
            "INSERT INTO household_members (household_id, user_id, joined_at) VALUES (@HouseholdId, @UserId, @Now)",
            new { HouseholdId = id, UserId = userId, Now = now }, ct));

        var categories = DefaultCategories.Select((c, i) => new
        {
            Id = Guid.CreateVersion7(),
            HouseholdId = id,
            c.Name,
            c.Color,
            c.Icon,
            SortOrder = i,
        });
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO categories (id, household_id, name, color, icon, sort_order, is_default)
            VALUES (@Id, @HouseholdId, @Name, @Color, @Icon, @SortOrder, true)
            """,
            categories, ct));

        var household = await LoadAsync(conn, id, ct) ?? throw new InvalidOperationException("Household vanished inside its own transaction");
        await tx.CommitAsync(ct);
        return household;
    }

    public async Task<bool> RenameAsync(Guid householdId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            "UPDATE households SET name = @Name WHERE id = @Id",
            new { Id = householdId, Name = name }, ct));
        return rows > 0;
    }

    /// <summary>Removes a member; the household is deleted (cascading everything) once nobody is left.</summary>
    public async Task LeaveAsync(Guid householdId, Guid userId, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(Cmd(
            "DELETE FROM household_members WHERE household_id = @HouseholdId AND user_id = @UserId",
            new { HouseholdId = householdId, UserId = userId }, ct));

        // The leaver's refresh tokens stay valid; their next refresh simply drops the hh claim.
        // Any live couple code they created is no longer theirs to hand out.
        await conn.ExecuteAsync(Cmd(
            "UPDATE couple_codes SET expires_at = @Now WHERE household_id = @HouseholdId AND created_by = @UserId AND redeemed_at IS NULL AND expires_at > @Now",
            new { HouseholdId = householdId, UserId = userId, Now = now }, ct));

        await conn.ExecuteAsync(Cmd(
            "DELETE FROM households h WHERE h.id = @HouseholdId AND NOT EXISTS (SELECT 1 FROM household_members m WHERE m.household_id = h.id)",
            new { HouseholdId = householdId }, ct));

        await tx.CommitAsync(ct);
    }

    private static async Task<HouseholdDto?> LoadAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var household = await conn.QuerySingleOrDefaultAsync<HouseholdRow>(Cmd(SelectHousehold, new { Id = householdId }, ct));
        if (household is null)
        {
            return null;
        }

        var members = await conn.QueryAsync<MemberRow>(Cmd(SelectMembers, new { Id = householdId }, ct));
        return new HouseholdDto(
            household.Id,
            household.Name,
            household.CreatedAt,
            members.Select(m => new MemberDto(m.UserId, m.DisplayName, m.Email, m.JoinedAt)).ToList());
    }
}
