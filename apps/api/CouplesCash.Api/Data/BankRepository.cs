using CouplesCash.Api.SimpleFin;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary><c>bank_links</c> + <c>external_accounts</c>. Every statement is household-scoped; the scheduler's cross-household list is the one exception and is read-only.</summary>
public sealed class BankRepository(NpgsqlDataSource db)
{
    private const string LinkColumns = "id, household_id, provider, name, access_url_enc, created_by, created_at, last_synced_at, last_manual_sync_at, last_attempt_at, last_error, bridge_errors, sync_enabled";

    private const string ExternalSelect =
        """
        SELECT e.id, e.bank_link_id, e.household_id, e.external_id, e.org_name, e.name, e.currency, e.balance_cents, e.available_cents,
               e.balance_at, e.account_id, e.created_at, e.updated_at, a.name AS account_name
        FROM external_accounts e
        LEFT JOIN accounts a ON a.id = e.account_id AND a.archived_at IS NULL
        """;

    // ---------------------------------------------------------------------------------------------
    // Links
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<BankLinkRow>> ListLinksAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BankLinkRow>(Cmd(
            $"SELECT {LinkColumns} FROM bank_links WHERE household_id = @HouseholdId ORDER BY created_at, id",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>Every link with sync enabled, across households (scheduler). The caller decides which are due.</summary>
    public async Task<IReadOnlyList<BankLinkRow>> ListSyncEnabledAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BankLinkRow>(Cmd(
            $"SELECT {LinkColumns} FROM bank_links WHERE sync_enabled ORDER BY last_synced_at NULLS FIRST, id", null, ct));
        return rows.ToList();
    }

    public async Task<BankLinkRow?> GetLinkAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetLinkAsync(conn, householdId, id, ct);
    }

    public Task<BankLinkRow?> GetLinkAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BankLinkRow>(Cmd(
            $"SELECT {LinkColumns} FROM bank_links WHERE id = @Id AND household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));

    public Task<BankLinkRow> InsertLinkAsync(NpgsqlConnection conn, Guid householdId, Guid userId, string name, byte[] accessUrlEnc, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<BankLinkRow>(Cmd(
            $"""
            INSERT INTO bank_links (id, household_id, provider, name, access_url_enc, created_by, created_at)
            VALUES (@Id, @HouseholdId, 'simplefin', @Name, @AccessUrlEnc, @UserId, @Now)
            RETURNING {LinkColumns}
            """,
            new { Id = Guid.CreateVersion7(), HouseholdId = householdId, Name = name, AccessUrlEnc = accessUrlEnc, UserId = userId, Now = now }, ct));

    public Task<BankLinkRow?> UpdateLinkAsync(NpgsqlConnection conn, Guid householdId, Guid id, string? name, bool? syncEnabled, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BankLinkRow>(Cmd(
            $"""
            UPDATE bank_links
            SET name = COALESCE(@Name, name), sync_enabled = COALESCE(@SyncEnabled, sync_enabled)
            WHERE id = @Id AND household_id = @HouseholdId
            RETURNING {LinkColumns}
            """,
            new { Id = id, HouseholdId = householdId, Name = name, SyncEnabled = syncEnabled }, ct));

    public Task<BankLinkRow?> DeleteLinkAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BankLinkRow>(Cmd(
            $"DELETE FROM bank_links WHERE id = @Id AND household_id = @HouseholdId RETURNING {LinkColumns}",
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>A successful sync: timestamps, Bridge errors (shown in the UI), cleared last_error; a manual sync also starts the cooldown.</summary>
    public Task<BankLinkRow?> MarkSyncedAsync(NpgsqlConnection conn, Guid householdId, Guid id, DateTime now, bool manual, IReadOnlyList<string> bridgeErrors, string? name, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BankLinkRow>(Cmd(
            $"""
            UPDATE bank_links
            SET last_synced_at = @Now,
                last_attempt_at = @Now,
                last_manual_sync_at = CASE WHEN @Manual THEN @Now ELSE last_manual_sync_at END,
                last_error = NULL,
                bridge_errors = @BridgeErrors,
                name = COALESCE(@Name, name)
            WHERE id = @Id AND household_id = @HouseholdId
            RETURNING {LinkColumns}
            """,
            new { Id = id, HouseholdId = householdId, Now = now, Manual = manual, BridgeErrors = bridgeErrors.ToArray(), Name = name }, ct));

    /// <summary>Records a failure on its own connection (the sync transaction has been rolled back).</summary>
    public async Task SetErrorAsync(Guid householdId, Guid id, string error, bool manual, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            """
            UPDATE bank_links
            SET last_error = @Error, last_attempt_at = @Now, last_manual_sync_at = CASE WHEN @Manual THEN @Now ELSE last_manual_sync_at END
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, Error = error, Manual = manual, Now = now }, ct));
    }

    /// <summary>(latest sync across the household's links, number of links) for the inbox summary.</summary>
    public async Task<(DateTime? LastSyncedAt, int LinkCount)> SyncSummaryAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<(DateTime? LastSyncedAt, int LinkCount)>(Cmd(
            "SELECT max(last_synced_at) AS last_synced_at, count(*)::int AS link_count FROM bank_links WHERE household_id = @HouseholdId",
            new { HouseholdId = householdId }, ct));
        return row;
    }

    // ---------------------------------------------------------------------------------------------
    // External accounts
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ExternalAccountRow>> ListExternalAccountsAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ExternalAccountRow>(Cmd(
            $"{ExternalSelect} WHERE e.household_id = @HouseholdId ORDER BY e.org_name, e.name, e.id",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    public async Task<ExternalAccountRow?> GetExternalAccountAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetExternalAccountAsync(conn, householdId, id, ct);
    }

    public Task<ExternalAccountRow?> GetExternalAccountAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExternalAccountRow>(Cmd(
            $"{ExternalSelect} WHERE e.id = @Id AND e.household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>Insert or refresh what the Bridge reports; the mapping (<c>account_id</c>) is left alone.</summary>
    public Task<ExternalAccountRow> UpsertExternalAccountAsync(NpgsqlConnection conn, Guid householdId, Guid linkId, SimpleFinAccount account, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<ExternalAccountRow>(Cmd(
            """
            WITH upserted AS (
                INSERT INTO external_accounts (id, bank_link_id, household_id, external_id, org_name, name, currency, balance_cents, available_cents, balance_at, created_at, updated_at)
                VALUES (@Id, @LinkId, @HouseholdId, @ExternalId, @OrgName, @Name, @Currency, @BalanceCents, @AvailableCents, @BalanceAt, @Now, @Now)
                ON CONFLICT (bank_link_id, external_id) DO UPDATE
                SET org_name = EXCLUDED.org_name, name = EXCLUDED.name, currency = EXCLUDED.currency, balance_cents = EXCLUDED.balance_cents,
                    available_cents = EXCLUDED.available_cents, balance_at = EXCLUDED.balance_at, updated_at = EXCLUDED.updated_at
                RETURNING *
            )
            SELECT e.id, e.bank_link_id, e.household_id, e.external_id, e.org_name, e.name, e.currency, e.balance_cents, e.available_cents,
                   e.balance_at, e.account_id, e.created_at, e.updated_at, a.name AS account_name
            FROM upserted e
            LEFT JOIN accounts a ON a.id = e.account_id AND a.archived_at IS NULL
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                LinkId = linkId,
                HouseholdId = householdId,
                ExternalId = account.Id,
                account.OrgName,
                account.Name,
                account.Currency,
                account.BalanceCents,
                account.AvailableCents,
                account.BalanceAt,
                Now = now,
            }, ct));

    public Task<ExternalAccountRow?> SetMappingAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid? accountId, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ExternalAccountRow>(Cmd(
            """
            WITH updated AS (
                UPDATE external_accounts SET account_id = @AccountId, updated_at = @Now
                WHERE id = @Id AND household_id = @HouseholdId
                RETURNING *
            )
            SELECT e.id, e.bank_link_id, e.household_id, e.external_id, e.org_name, e.name, e.currency, e.balance_cents, e.available_cents,
                   e.balance_at, e.account_id, e.created_at, e.updated_at, a.name AS account_name
            FROM updated e
            LEFT JOIN accounts a ON a.id = e.account_id AND a.archived_at IS NULL
            """,
            new { Id = id, HouseholdId = householdId, AccountId = accountId, Now = now }, ct));

    /// <summary>Accounts that receive bank data (for transfer detection).</summary>
    public async Task<IReadOnlySet<Guid>> MappedAccountIdsAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Guid>(Cmd(
            "SELECT DISTINCT account_id FROM external_accounts WHERE household_id = @HouseholdId AND account_id IS NOT NULL",
            new { HouseholdId = householdId }, ct));
        return rows.ToHashSet();
    }

    /// <summary>
    /// Writes a Bridge balance onto the mapped account when it differs (sign already converted for liabilities) and records a
    /// snapshot. Returns true when the balance changed. <paramref name="recordedBy"/> is the user who linked the bank for scheduler syncs.
    /// </summary>
    public async Task<bool> ApplyBalanceAsync(NpgsqlConnection conn, Guid householdId, Guid accountId, long balanceCents, Guid recordedBy, DateTime now, CancellationToken ct)
    {
        var changed = await conn.ExecuteAsync(Cmd(
            """
            UPDATE accounts SET balance_cents = @BalanceCents, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL AND balance_cents <> @BalanceCents
            """,
            new { Id = accountId, HouseholdId = householdId, BalanceCents = balanceCents, Now = now }, ct));
        if (changed == 0)
        {
            return false;
        }

        await conn.ExecuteAsync(Cmd(
            "INSERT INTO account_balance_snapshots (id, account_id, balance_cents, recorded_at, recorded_by) VALUES (@Id, @AccountId, @BalanceCents, @Now, @UserId)",
            new { Id = Guid.CreateVersion7(), AccountId = accountId, BalanceCents = balanceCents, Now = now, UserId = recordedBy }, ct));
        return true;
    }
}
