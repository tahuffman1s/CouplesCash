using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>Settings changes for PATCH /v1/couple. Null = leave as is; the <see cref="Optional{T}"/> fields can be cleared.</summary>
public sealed record HouseholdSettingsUpdate(
    string? Name,
    string? Currency,
    int? WeekStart,
    string? Timezone,
    Optional<Guid?> DefaultAccountId,
    PayPeriod? PayPeriod,
    Optional<DateOnly?> PayAnchorDate,
    Optional<long?> WeeklySpendCents = default,
    bool? AutoIgnoreTransfers = null,
    AutoMatchLevel? AutoMatchLevel = null,
    int? PaydayWeekday = null);

public sealed class HouseholdRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, name, created_at, currency, week_start, timezone, default_account_id, pay_period, pay_anchor_date, weekly_spend_cents, auto_ignore_transfers, auto_match_level, payday_weekday";

    private const string SelectHousehold = $"SELECT {Columns} FROM households WHERE id = @Id";

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

    public Task<HouseholdDto?> GetAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) => LoadAsync(conn, householdId, ct);

    /// <summary>The household's settings row (currency, time zone, pay period…). Null when the household does not exist.</summary>
    public async Task<HouseholdRow?> GetRowAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<HouseholdRow>(Cmd(SelectHousehold, new { Id = householdId }, ct));
    }

    public Task<HouseholdRow?> GetRowAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<HouseholdRow>(Cmd(SelectHousehold, new { Id = householdId }, ct));

    /// <summary>Every household (for the scheduler).</summary>
    public async Task<IReadOnlyList<HouseholdRow>> ListAllAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<HouseholdRow>(Cmd($"SELECT {Columns} FROM households ORDER BY id", null, ct));
        return rows.ToList();
    }

    /// <summary>v6: the member that system actions are recorded under where a user id is required (expenses.created_by): the first to join.</summary>
    public Task<Guid?> FirstMemberAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<Guid?>(Cmd(
            "SELECT user_id FROM household_members WHERE household_id = @Id ORDER BY joined_at, user_id LIMIT 1",
            new { Id = householdId }, ct));

    /// <summary>Member user ids of a household.</summary>
    public async Task<IReadOnlyList<Guid>> MemberIdsAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(Cmd(
            "SELECT user_id FROM household_members WHERE household_id = @Id ORDER BY joined_at",
            new { Id = householdId }, ct));
        return rows.ToList();
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

        await SeedCategoriesAsync(conn, id, ct);

        var household = await LoadAsync(conn, id, ct) ?? throw new InvalidOperationException("Household vanished inside its own transaction");
        await tx.CommitAsync(ct);
        return household;
    }

    private static Task SeedCategoriesAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO categories (id, household_id, name, color, icon, sort_order, is_default)
            VALUES (@Id, @HouseholdId, @Name, @Color, @Icon, @SortOrder, true)
            """,
            DefaultCategories.Select((c, i) => new { Id = Guid.CreateVersion7(), HouseholdId = householdId, c.Name, c.Color, c.Icon, SortOrder = i }),
            ct));

    /// <summary>
    /// "Start over": deletes every piece of the household's money data — accounts (with balance history), bills and their payments,
    /// bank links (stored credentials included), transactions, sweeps, rules, expenses, budgets, goals, to-dos, categories and the
    /// activity feed — and puts the settings back to their defaults (name, currency and time zone stay). Members, logins, passkeys,
    /// push subscriptions and notification preferences are untouched. The default categories are seeded again. Caller commits.
    /// </summary>
    public async Task ResetDataAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        // Children first where a delete would otherwise only null a reference; the rest cascade (payments, skips, reminders,
        // balance snapshots, goal contributions, external accounts).
        string[] tables =
        [
            "sweeps", "imported_transactions", "csv_imports", "bank_links", "external_accounts", "bill_suggestions", "payee_rules",
            "budgets", "expenses", "todos", "savings_goals", "recurring_items", "accounts", "categories", "activity_events",
        ];
        foreach (var table in tables)
        {
            await conn.ExecuteAsync(Cmd($"DELETE FROM {table} WHERE household_id = @Id", new { Id = householdId }, ct));
        }

        await conn.ExecuteAsync(Cmd(
            """
            UPDATE households
            SET default_account_id = NULL, week_start = 0, pay_period = 'monthly', pay_anchor_date = NULL, weekly_spend_cents = NULL,
                auto_ignore_transfers = true, auto_match_level = 'all', payday_weekday = 5
            WHERE id = @Id
            """,
            new { Id = householdId }, ct));
        await SeedCategoriesAsync(conn, householdId, ct);
    }

    /// <summary>Row lock on the household for the length of the caller's transaction (serialises a reset with other writers of it).</summary>
    public Task LockAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd("SELECT 1 FROM households WHERE id = @Id FOR UPDATE", new { Id = householdId }, ct));

    /// <summary>Applies a settings patch. Returns false when the household does not exist.</summary>
    public async Task<bool> UpdateSettingsAsync(NpgsqlConnection conn, Guid householdId, HouseholdSettingsUpdate update, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(Cmd(
            """
            UPDATE households
            SET name               = COALESCE(@Name, name),
                currency           = COALESCE(@Currency, currency),
                week_start         = COALESCE(@WeekStart, week_start),
                timezone           = COALESCE(@Timezone, timezone),
                pay_period         = COALESCE(@PayPeriod, pay_period),
                default_account_id = CASE WHEN @SetDefaultAccount THEN @DefaultAccountId ELSE default_account_id END,
                pay_anchor_date    = CASE WHEN @SetPayAnchor THEN @PayAnchorDate ELSE pay_anchor_date END,
                weekly_spend_cents = CASE WHEN @SetWeeklySpend THEN @WeeklySpendCents ELSE weekly_spend_cents END,
                auto_ignore_transfers = COALESCE(@AutoIgnoreTransfers, auto_ignore_transfers),
                auto_match_level   = COALESCE(@AutoMatchLevel, auto_match_level),
                payday_weekday     = COALESCE(@PaydayWeekday, payday_weekday)
            WHERE id = @Id
            """,
            new
            {
                Id = householdId,
                update.Name,
                update.Currency,
                WeekStart = (short?)update.WeekStart,
                update.Timezone,
                PayPeriod = update.PayPeriod?.ToDb(),
                SetDefaultAccount = update.DefaultAccountId.HasValue,
                DefaultAccountId = update.DefaultAccountId.Value,
                SetPayAnchor = update.PayAnchorDate.HasValue,
                PayAnchorDate = update.PayAnchorDate.Value,
                SetWeeklySpend = update.WeeklySpendCents.HasValue,
                WeeklySpendCents = update.WeeklySpendCents.Value,
                update.AutoIgnoreTransfers,
                AutoMatchLevel = update.AutoMatchLevel?.ToDb(),
                PaydayWeekday = (short?)update.PaydayWeekday,
            }, ct));
        return rows > 0;
    }

    /// <summary>Removes a member; the household is deleted (cascading everything) once nobody is left. Returns true when the household was deleted.</summary>
    public async Task<bool> LeaveAsync(NpgsqlConnection conn, Guid householdId, Guid userId, DateTime now, CancellationToken ct)
    {
        await conn.ExecuteAsync(Cmd(
            "DELETE FROM household_members WHERE household_id = @HouseholdId AND user_id = @UserId",
            new { HouseholdId = householdId, UserId = userId }, ct));

        // The leaver's refresh tokens stay valid; their next refresh simply drops the hh claim.
        // Any live couple code they created is no longer theirs to hand out.
        await conn.ExecuteAsync(Cmd(
            "UPDATE couple_codes SET expires_at = @Now WHERE household_id = @HouseholdId AND created_by = @UserId AND redeemed_at IS NULL AND expires_at > @Now",
            new { HouseholdId = householdId, UserId = userId, Now = now }, ct));

        var deleted = await conn.ExecuteAsync(Cmd(
            "DELETE FROM households h WHERE h.id = @HouseholdId AND NOT EXISTS (SELECT 1 FROM household_members m WHERE m.household_id = h.id)",
            new { HouseholdId = householdId }, ct));
        return deleted > 0;
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
            members.Select(m => new MemberDto(m.UserId, m.DisplayName, m.Email, m.JoinedAt)).ToList(),
            household.Currency,
            household.PaydayWeekday, // v7: weekStart is derived from the payday (kept for compatibility)
            household.Timezone,
            household.DefaultAccountId,
            household.PayPeriod,
            household.PayAnchorDate,
            household.WeeklySpendCents,
            household.AutoIgnoreTransfers,
            household.AutoMatchLevel,
            household.PaydayWeekday);
    }
}
