using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed record RecurringFields(
    string Name,
    Kind Kind,
    long AmountCents,
    Guid? CategoryId,
    Guid? AccountId,
    Frequency Frequency,
    DateOnly AnchorDate,
    DateOnly? EndDate,
    bool AutoPay,
    string? Notes,
    string? Url);

public sealed class RecurringRepository(NpgsqlDataSource db)
{
    private const string Select =
        """
        SELECT i.id, i.household_id, i.name, i.kind, i.amount_cents, i.category_id, i.account_id, i.frequency,
               i.anchor_date, i.end_date, i.auto_pay, i.notes, i.url, i.created_by, i.created_at, i.updated_at, i.archived_at,
               c.name AS category_name, c.color AS category_color, a.name AS account_name
        FROM recurring_items i
        LEFT JOIN categories c ON c.id = i.category_id
        LEFT JOIN accounts a ON a.id = i.account_id
        """;

    public async Task<IReadOnlyList<RecurringItemRow>> ListAsync(Guid householdId, Kind? kind, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RecurringItemRow>(Cmd(
            $"""
            {Select}
            WHERE i.household_id = @HouseholdId AND i.archived_at IS NULL AND (@Kind::text IS NULL OR i.kind = @Kind)
            ORDER BY i.name, i.id
            """,
            new { HouseholdId = householdId, Kind = kind?.ToDb() }, ct));
        return rows.ToList();
    }

    public async Task<RecurringItemRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, id, ct);
    }

    public async Task<RecurringItemRow> CreateAsync(Guid householdId, Guid userId, RecurringFields f, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO recurring_items (id, household_id, name, kind, amount_cents, category_id, account_id, frequency,
                                         anchor_date, end_date, auto_pay, notes, url, created_by, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Name, @Kind, @AmountCents, @CategoryId, @AccountId, @Frequency,
                    @AnchorDate, @EndDate, @AutoPay, @Notes, @Url, @UserId, @Now, @Now)
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                f.Name,
                Kind = f.Kind.ToDb(),
                f.AmountCents,
                f.CategoryId,
                f.AccountId,
                Frequency = f.Frequency.ToDb(),
                f.AnchorDate,
                f.EndDate,
                f.AutoPay,
                f.Notes,
                f.Url,
                UserId = userId,
                Now = now,
            }, ct));

        return await GetAsync(conn, householdId, id, ct) ?? throw new InvalidOperationException("Recurring item vanished after insert");
    }

    public async Task<RecurringItemRow?> UpdateAsync(Guid householdId, Guid id, RecurringFields f, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            """
            UPDATE recurring_items
            SET name = @Name, kind = @Kind, amount_cents = @AmountCents, category_id = @CategoryId, account_id = @AccountId,
                frequency = @Frequency, anchor_date = @AnchorDate, end_date = @EndDate, auto_pay = @AutoPay,
                notes = @Notes, url = @Url, updated_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                f.Name,
                Kind = f.Kind.ToDb(),
                f.AmountCents,
                f.CategoryId,
                f.AccountId,
                Frequency = f.Frequency.ToDb(),
                f.AnchorDate,
                f.EndDate,
                f.AutoPay,
                f.Notes,
                f.Url,
                Now = now,
            }, ct));

        return rows == 0 ? null : await GetAsync(conn, householdId, id, ct);
    }

    public async Task<bool> ArchiveAsync(Guid householdId, Guid id, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            "UPDATE recurring_items SET archived_at = @Now, updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId AND archived_at IS NULL",
            new { Id = id, HouseholdId = householdId, Now = now }, ct));
        return rows > 0;
    }

    /// <summary>Payments of the household's items due within [from, to].</summary>
    public async Task<IReadOnlyList<PaymentRow>> ListPaymentsAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PaymentRow>(Cmd(
            """
            SELECT p.recurring_item_id, p.due_date, p.paid_at, p.paid_by, p.amount_cents
            FROM occurrence_payments p
            JOIN recurring_items i ON i.id = p.recurring_item_id
            WHERE i.household_id = @HouseholdId AND p.due_date BETWEEN @From AND @To
            """,
            new { HouseholdId = householdId, From = from, To = to }, ct));
        return rows.ToList();
    }

    public async Task<PaymentRow> UpsertPaymentAsync(Guid itemId, DateOnly dueDate, Guid userId, long amountCents, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<PaymentRow>(Cmd(
            """
            INSERT INTO occurrence_payments (recurring_item_id, due_date, paid_at, paid_by, amount_cents)
            VALUES (@ItemId, @DueDate, @Now, @UserId, @AmountCents)
            ON CONFLICT (recurring_item_id, due_date)
            DO UPDATE SET paid_at = EXCLUDED.paid_at, paid_by = EXCLUDED.paid_by, amount_cents = EXCLUDED.amount_cents
            RETURNING recurring_item_id, due_date, paid_at, paid_by, amount_cents
            """,
            new { ItemId = itemId, DueDate = dueDate, Now = now, UserId = userId, AmountCents = amountCents }, ct));
    }

    public async Task DeletePaymentAsync(Guid itemId, DateOnly dueDate, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "DELETE FROM occurrence_payments WHERE recurring_item_id = @ItemId AND due_date = @DueDate",
            new { ItemId = itemId, DueDate = dueDate }, ct));
    }

    private static Task<RecurringItemRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<RecurringItemRow>(Cmd(
            $"{Select} WHERE i.id = @Id AND i.household_id = @HouseholdId AND i.archived_at IS NULL",
            new { Id = id, HouseholdId = householdId }, ct));
}
