using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed record GoalFields(string Name, long TargetCents, DateOnly? TargetDate, Guid? AccountId, string Color, string Icon);

public sealed class GoalRepository(NpgsqlDataSource db)
{
    private const string Select =
        """
        SELECT g.id, g.household_id, g.name, g.target_cents, g.target_date, g.account_id, g.saved_cents, g.color, g.icon,
               g.created_by, g.created_at, g.updated_at, g.archived_at,
               a.name AS account_name, a.balance_cents AS account_balance_cents
        FROM savings_goals g
        LEFT JOIN accounts a ON a.id = g.account_id
        """;

    public async Task<IReadOnlyList<GoalRow>> ListAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GoalRow>(Cmd(
            $"{Select} WHERE g.household_id = @HouseholdId AND g.archived_at IS NULL ORDER BY g.created_at, g.id",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    public async Task<GoalRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, id, ct);
    }

    public Task<GoalRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<GoalRow>(Cmd(
            $"{Select} WHERE g.id = @Id AND g.household_id = @HouseholdId AND g.archived_at IS NULL",
            new { Id = id, HouseholdId = householdId }, ct));

    public async Task<GoalRow> CreateAsync(NpgsqlConnection conn, Guid householdId, Guid userId, GoalFields f, DateTime now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO savings_goals (id, household_id, name, target_cents, target_date, account_id, saved_cents, color, icon, created_by, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Name, @TargetCents, @TargetDate, @AccountId, 0, @Color, @Icon, @UserId, @Now, @Now)
            """,
            new { Id = id, HouseholdId = householdId, f.Name, f.TargetCents, f.TargetDate, f.AccountId, f.Color, f.Icon, UserId = userId, Now = now }, ct));
        return await GetAsync(conn, householdId, id, ct) ?? throw new InvalidOperationException("Goal vanished after insert");
    }

    public async Task<GoalRow?> UpdateAsync(NpgsqlConnection conn, Guid householdId, Guid id, GoalFields f, DateTime now, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            """
            UPDATE savings_goals
            SET name = @Name, target_cents = @TargetCents, target_date = @TargetDate, account_id = @AccountId, color = @Color, icon = @Icon, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL
            """,
            new { Id = id, HouseholdId = householdId, f.Name, f.TargetCents, f.TargetDate, f.AccountId, f.Color, f.Icon, Now = now }, ct));
        return rows == 0 ? null : await GetAsync(conn, householdId, id, ct);
    }

    public async Task<bool> ArchiveAsync(NpgsqlConnection conn, Guid householdId, Guid id, DateTime now, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            "UPDATE savings_goals SET archived_at = @Now, updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL",
            new { Id = id, HouseholdId = householdId, Now = now }, ct));
        return rows > 0;
    }

    /// <summary>Adds a manual contribution and bumps saved_cents. The caller has checked the goal is manual and in the household.</summary>
    public async Task<GoalRow?> ContributeAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid userId, long amountCents, DateTime now, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            """
            UPDATE savings_goals SET saved_cents = saved_cents + @Amount, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL AND account_id IS NULL
            """,
            new { Id = id, HouseholdId = householdId, Amount = amountCents, Now = now }, ct));
        if (rows == 0)
        {
            return null;
        }

        await conn.ExecuteAsync(Cmd(
            "INSERT INTO goal_contributions (id, goal_id, amount_cents, contributed_by, created_at) VALUES (@Id, @GoalId, @Amount, @UserId, @Now)",
            new { Id = Guid.CreateVersion7(), GoalId = id, Amount = amountCents, UserId = userId, Now = now }, ct));
        return await GetAsync(conn, householdId, id, ct);
    }

    /// <summary>Contributions of the given goals, oldest first (for running-sum history).</summary>
    public async Task<IReadOnlyList<ContributionRow>> ContributionsAsync(Guid householdId, IReadOnlyCollection<Guid> goalIds, CancellationToken ct)
    {
        if (goalIds.Count == 0)
        {
            return [];
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ContributionRow>(Cmd(
            """
            SELECT c.id, c.goal_id, c.amount_cents, c.contributed_by, c.created_at
            FROM goal_contributions c
            JOIN savings_goals g ON g.id = c.goal_id
            WHERE g.household_id = @HouseholdId AND c.goal_id = ANY(@GoalIds)
            ORDER BY c.created_at, c.id
            """,
            new { HouseholdId = householdId, GoalIds = goalIds.ToArray() }, ct));
        return rows.ToList();
    }
}
