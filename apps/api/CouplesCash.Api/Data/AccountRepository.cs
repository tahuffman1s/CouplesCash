using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed record AccountFields(string Name, string Institution, AccountType Type, long BalanceCents, string Color, string Icon, string? Note);

/// <summary>
/// Reads open their own connection. Mutations take the caller's connection (inside a <see cref="UnitOfWork"/>)
/// so the activity event written alongside them shares the transaction.
/// </summary>
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
        return await GetAsync(conn, householdId, id, ct);
    }

    public Task<AccountRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<AccountRow>(Cmd(
            $"SELECT {Columns} FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL",
            new { Id = id, HouseholdId = householdId }, ct));

    public async Task<bool> ExistsAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await ExistsAsync(conn, householdId, id, ct);
    }

    public Task<bool> ExistsAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(Cmd(
            "SELECT EXISTS (SELECT 1 FROM accounts WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL)",
            new { Id = id, HouseholdId = householdId }, ct));

    public async Task<AccountRow> CreateAsync(NpgsqlConnection conn, Guid householdId, Guid userId, AccountFields fields, DateTime now, CancellationToken ct)
    {
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
        return row;
    }

    /// <summary>Full update. A changed balance is recorded as a snapshot. Returns null when the account is not in the household.</summary>
    public async Task<AccountRow?> UpdateAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid userId, AccountFields fields, DateTime now, CancellationToken ct)
    {
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

        return row;
    }

    public async Task<AccountRow?> SetBalanceAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid userId, long balanceCents, DateTime now, CancellationToken ct)
    {
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
        return row;
    }

    /// <summary>Adds <paramref name="deltaCents"/> to the balance (expenses) and records a snapshot. Null when the account is gone.</summary>
    public async Task<AccountRow?> AdjustBalanceAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid userId, long deltaCents, DateTime now, CancellationToken ct)
    {
        if (deltaCents == 0)
        {
            return await GetAsync(conn, householdId, id, ct);
        }

        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(Cmd(
            $"""
            UPDATE accounts SET balance_cents = balance_cents + @Delta, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL
            RETURNING {Columns}
            """,
            new { Id = id, HouseholdId = householdId, Delta = deltaCents, Now = now }, ct));
        if (row is null)
        {
            return null;
        }

        await InsertSnapshotAsync(conn, id, row.BalanceCents, userId, now, ct);
        return row;
    }

    /// <summary>Soft delete. Returns the archived row, or null when it is not in the household.</summary>
    public Task<AccountRow?> ArchiveAsync(NpgsqlConnection conn, Guid householdId, Guid id, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<AccountRow>(Cmd(
            $"""
            UPDATE accounts SET archived_at = @Now, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL
            RETURNING {Columns}
            """,
            new { Id = id, HouseholdId = householdId, Now = now }, ct));

    /// <summary>Newest first. Returns null when the account is not in the household.</summary>
    public async Task<IReadOnlyList<SnapshotRow>?> HistoryAsync(Guid householdId, Guid id, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var exists = await ExistsAsync(conn, householdId, id, ct);
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

    /// <summary>The latest <paramref name="perAccount"/> snapshots of each given account, oldest first per account (goal history).</summary>
    public async Task<IReadOnlyList<AccountSnapshotPointRow>> RecentSnapshotsAsync(Guid householdId, IReadOnlyCollection<Guid> accountIds, int perAccount, CancellationToken ct)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AccountSnapshotPointRow>(Cmd(
            """
            SELECT account_id, balance_cents, recorded_at
            FROM (
                SELECT s.account_id, s.balance_cents, s.recorded_at,
                       row_number() OVER (PARTITION BY s.account_id ORDER BY s.recorded_at DESC, s.id DESC) AS rn
                FROM account_balance_snapshots s
                JOIN accounts a ON a.id = s.account_id
                WHERE a.household_id = @HouseholdId AND s.account_id = ANY(@AccountIds)
            ) ranked
            WHERE rn <= @PerAccount
            ORDER BY account_id, recorded_at, balance_cents
            """,
            new { HouseholdId = householdId, AccountIds = accountIds.ToArray(), PerAccount = perAccount }, ct));
        return rows.ToList();
    }

    /// <summary>
    /// Net worth as of <paramref name="before"/>: the latest snapshot of each active account recorded strictly before
    /// that instant, liabilities subtracted. Null when no account has a snapshot that old.
    /// </summary>
    public async Task<long?> NetWorthBeforeAsync(Guid householdId, DateTime before, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Type, long BalanceCents)>(Cmd(
            """
            SELECT a.type, s.balance_cents
            FROM accounts a
            JOIN LATERAL (
                SELECT balance_cents
                FROM account_balance_snapshots
                WHERE account_id = a.id AND recorded_at < @Before
                ORDER BY recorded_at DESC, id DESC
                LIMIT 1
            ) s ON true
            WHERE a.household_id = @HouseholdId AND a.archived_at IS NULL
            """,
            new { HouseholdId = householdId, Before = before }, ct));

        var list = rows.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        return list.Sum(r => Enum.Parse<AccountType>(r.Type, ignoreCase: true).IsLiability() ? -r.BalanceCents : r.BalanceCents);
    }

    private static Task<int> InsertSnapshotAsync(NpgsqlConnection conn, Guid accountId, long balanceCents, Guid userId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "INSERT INTO account_balance_snapshots (id, account_id, balance_cents, recorded_at, recorded_by) VALUES (@Id, @AccountId, @BalanceCents, @Now, @UserId)",
            new { Id = Guid.CreateVersion7(), AccountId = accountId, BalanceCents = balanceCents, Now = now, UserId = userId }, ct));
}
