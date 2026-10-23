using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed record AccountFields(string Name, string Institution, AccountType Type, long BalanceCents, string Color, string Icon, string? Note);

public sealed class AccountRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, household_id, name, institution, type, balance_cents, color, icon, note, created_by, created_at, updated_at, archived_at";

    public async Task<IReadOnlyList<AccountRow>> ListAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AccountRow>(Cmd(
            $"SELECT {Columns} FROM accounts WHERE household_id = @HouseholdId AND archived_at IS NULL",
            new { HouseholdId = householdId }, ct));
        return rows.OrderBy(a => a.Type).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<AccountRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AccountRow>(Cmd(
            $"SELECT {Columns} FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL",
            new { Id = id, HouseholdId = householdId }, ct));
    }

    public async Task<bool> ExistsAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(Cmd(
            "SELECT EXISTS (SELECT 1 FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL)",
            new { Id = id, HouseholdId = householdId }, ct));
    }

    public async Task<AccountRow> CreateAsync(Guid householdId, Guid userId, AccountFields fields, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var id = Guid.CreateVersion7();
        var row = await conn.QuerySingleAsync<AccountRow>(Cmd(
            $"""
            INSERT INTO accounts (id, household_id, name, institution, type, balance_cents, color, icon, note, created_by, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Name, @Institution, @Type, @BalanceCents, @Color, @Icon, @Note, @UserId, @Now, @Now)
            RETURNING {Columns}
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                fields.Name,
                fields.Institution,
                Type = fields.Type.ToDb(),
                fields.BalanceCents,
                fields.Color,
                fields.Icon,
                fields.Note,
                UserId = userId,
                Now = now,
            }, ct));

        await InsertSnapshotAsync(conn, id, fields.BalanceCents, userId, now, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    /// <summary>Full update. A changed balance is recorded as a snapshot. Returns null when the account is not in the household.</summary>
    public async Task<AccountRow?> UpdateAsync(Guid householdId, Guid id, Guid userId, AccountFields fields, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var previousBalance = await conn.QuerySingleOrDefaultAsync<long?>(Cmd(
            "SELECT balance_cents FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL FOR UPDATE",
            new { Id = id, HouseholdId = householdId }, ct));
        if (previousBalance is null)
        {
            return null;
        }

        var row = await conn.QuerySingleAsync<AccountRow>(Cmd(
            $"""
            UPDATE accounts
            SET name = @Name, institution = @Institution, type = @Type, balance_cents = @BalanceCents,
                color = @Color, icon = @Icon, note = @Note, updated_at = @Now
            WHERE id = @Id
            RETURNING {Columns}
            """,
            new
            {
                Id = id,
                fields.Name,
                fields.Institution,
                Type = fields.Type.ToDb(),
                fields.BalanceCents,
                fields.Color,
                fields.Icon,
                fields.Note,
                Now = now,
            }, ct));

        if (previousBalance.Value != fields.BalanceCents)
        {
            await InsertSnapshotAsync(conn, id, fields.BalanceCents, userId, now, ct);
        }

        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<AccountRow?> SetBalanceAsync(Guid householdId, Guid id, Guid userId, long balanceCents, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(Cmd(
            $"""
            UPDATE accounts SET balance_cents = @BalanceCents, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL
            RETURNING {Columns}
            """,
            new { Id = id, HouseholdId = householdId, BalanceCents = balanceCents, Now = now }, ct));
        if (row is null)
        {
            return null;
        }

        await InsertSnapshotAsync(conn, id, balanceCents, userId, now, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<bool> ArchiveAsync(Guid householdId, Guid id, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            "UPDATE accounts SET archived_at = @Now, updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL",
            new { Id = id, HouseholdId = householdId, Now = now }, ct));
        return rows > 0;
    }

    /// <summary>Newest first. Returns null when the account is not in the household.</summary>
    public async Task<IReadOnlyList<SnapshotRow>?> HistoryAsync(Guid householdId, Guid id, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(Cmd(
            "SELECT EXISTS (SELECT 1 FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL)",
            new { Id = id, HouseholdId = householdId }, ct));
        if (!exists)
        {
            return null;
        }

        var rows = await conn.QueryAsync<SnapshotRow>(Cmd(
            """
            SELECT balance_cents, recorded_at, recorded_by
            FROM account_balance_snapshots
            WHERE account_id = @Id
            ORDER BY recorded_at DESC, id DESC
            LIMIT @Limit
            """,
            new { Id = id, Limit = limit }, ct));
        return rows.ToList();
    }

    private static Task<int> InsertSnapshotAsync(NpgsqlConnection conn, Guid accountId, long balanceCents, Guid userId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "INSERT INTO account_balance_snapshots (id, account_id, balance_cents, recorded_at, recorded_by) VALUES (@Id, @AccountId, @BalanceCents, @Now, @UserId)",
            new { Id = Guid.CreateVersion7(), AccountId = accountId, BalanceCents = balanceCents, Now = now, UserId = userId }, ct));
}
