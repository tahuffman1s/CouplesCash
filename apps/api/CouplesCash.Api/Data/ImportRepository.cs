using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>What a sync or CSV import wants to insert; the repository dedupes on <c>(household_id, fingerprint)</c>.</summary>
public sealed record NewImportedTransaction(
    string Source,
    Guid? ExternalAccountId,
    Guid? AccountId,
    string? ExternalId,
    byte[] Fingerprint,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string? Memo,
    bool Pending);

/// <summary>Keyset cursor for the inbox list: rows strictly before (<paramref name="PostedOn"/>, <paramref name="Id"/>) in newest-first order.</summary>
public readonly record struct InboxCursor(DateOnly PostedOn, Guid Id);

/// <summary><c>imported_transactions</c> (the inbox), <c>payee_rules</c> and <c>csv_imports</c>.</summary>
public sealed class ImportRepository(NpgsqlDataSource db)
{
    private const string Select =
        """
        SELECT t.id, t.household_id, t.source, t.external_account_id, t.account_id, t.external_id, t.posted_on, t.amount_cents,
               t.description, t.payee, t.memo, t.pending, t.status, t.suggested_category_id, t.suggested_item_id, t.suggested_due_date,
               t.suggested_transfer, t.confidence, t.confirmed_expense_id, t.confirmed_item_id, t.confirmed_due_date, t.confirmed_auto,
               t.reviewed_by, t.reviewed_at, t.created_at,
               a.name AS account_name, c.name AS suggested_category_name, i.name AS suggested_item_name, i.kind AS suggested_item_kind,
               u.display_name AS reviewed_by_name, cc.name AS confirmed_category_name, ci.name AS confirmed_item_name
        FROM imported_transactions t
        LEFT JOIN accounts a ON a.id = t.account_id
        LEFT JOIN categories c ON c.id = t.suggested_category_id
        LEFT JOIN recurring_items i ON i.id = t.suggested_item_id
        LEFT JOIN users u ON u.id = t.reviewed_by
        LEFT JOIN expenses ex ON ex.id = t.confirmed_expense_id
        LEFT JOIN categories cc ON cc.id = ex.category_id
        LEFT JOIN recurring_items ci ON ci.id = t.confirmed_item_id
        """;

    // ---------------------------------------------------------------------------------------------
    // Inserts
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Inserts unless the fingerprint exists. A SimpleFIN row that was pending and now posted is refreshed in place
    /// (same id, flipped flag) and still counts as not new. Returns the new row id, or null for a duplicate.
    /// </summary>
    public Task<Guid?> InsertAsync(NpgsqlConnection conn, Guid householdId, NewImportedTransaction t, DateTime now, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<Guid?>(Cmd(
            """
            INSERT INTO imported_transactions (id, household_id, source, external_account_id, account_id, external_id, fingerprint, posted_on,
                                               amount_cents, description, payee, memo, pending, status, created_at)
            VALUES (@Id, @HouseholdId, @Source, @ExternalAccountId, @AccountId, @ExternalId, @Fingerprint, @PostedOn,
                    @AmountCents, @Description, @Payee, @Memo, @Pending, 'pending', @Now)
            ON CONFLICT (household_id, fingerprint) DO UPDATE
            SET pending = EXCLUDED.pending, posted_on = EXCLUDED.posted_on, amount_cents = EXCLUDED.amount_cents,
                description = EXCLUDED.description, payee = EXCLUDED.payee, memo = EXCLUDED.memo
            WHERE imported_transactions.pending AND NOT EXCLUDED.pending
            RETURNING CASE WHEN xmax = 0 THEN id END
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                HouseholdId = householdId,
                t.Source,
                t.ExternalAccountId,
                t.AccountId,
                t.ExternalId,
                t.Fingerprint,
                t.PostedOn,
                t.AmountCents,
                t.Description,
                t.Payee,
                t.Memo,
                t.Pending,
                Now = now,
            }, ct));

    // ---------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------

    public async Task<ImportedTransactionRow?> GetAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, householdId, id, ct);
    }

    public Task<ImportedTransactionRow?> GetAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.id = @Id AND t.household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>Row lock for a review action so two taps on "Confirm" cannot both create an expense.</summary>
    public Task<int> LockAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "SELECT 1 FROM imported_transactions WHERE id = @Id AND household_id = @HouseholdId FOR UPDATE",
            new { Id = id, HouseholdId = householdId }, ct));

    public async Task<IReadOnlyList<ImportedTransactionRow>> ListByIdsAsync(NpgsqlConnection conn, Guid householdId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.household_id = @HouseholdId AND t.id = ANY(@Ids) ORDER BY t.posted_on, t.id",
            new { HouseholdId = householdId, Ids = ids.ToArray() }, ct));
        return rows.ToList();
    }

    /// <summary>Newest posted_on first, then newest id; returns one row more than <paramref name="limit"/> so the caller can build the cursor.</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListAsync(Guid householdId, string? status, InboxCursor? before, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId
              AND (@Status::text IS NULL OR t.status = @Status)
              AND (@BeforeDate::date IS NULL OR (t.posted_on, t.id) < (@BeforeDate, @BeforeId))
            ORDER BY t.posted_on DESC, t.id DESC
            LIMIT @Limit
            """,
            new { HouseholdId = householdId, Status = status, BeforeDate = before?.PostedOn, BeforeId = before?.Id ?? Guid.Empty, Limit = limit + 1 }, ct));
        return rows.ToList();
    }

    /// <summary>All pending rows of the household (bulk actions without ids), oldest first.</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListPendingAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.household_id = @HouseholdId AND t.status = 'pending' ORDER BY t.posted_on, t.id",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>Pending rows posted within [from, to] (other legs of a transfer).</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListPendingInWindowAsync(NpgsqlConnection conn, Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.household_id = @HouseholdId AND t.status = 'pending' AND t.posted_on BETWEEN @From AND @To ORDER BY t.posted_on, t.id",
            new { HouseholdId = householdId, From = from, To = to }, ct));
        return rows.ToList();
    }

    public Task<ImportedTransactionRow?> FindConfirmedForOccurrenceAsync(NpgsqlConnection conn, Guid householdId, Guid itemId, DateOnly dueDate, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId AND t.status = 'confirmed' AND t.confirmed_item_id = @ItemId AND t.confirmed_due_date = @DueDate
            ORDER BY t.reviewed_at DESC NULLS LAST, t.id DESC
            LIMIT 1
            """,
            new { HouseholdId = householdId, ItemId = itemId, DueDate = dueDate }, ct));

    public async Task<(int Pending, int Suggested)> SummaryAsync(Guid householdId, int bulkConfidence, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<(int Pending, int Suggested)>(Cmd(
            """
            SELECT count(*)::int AS pending,
                   count(*) FILTER (WHERE confidence >= @Min AND (suggested_category_id IS NOT NULL OR suggested_item_id IS NOT NULL OR suggested_transfer))::int AS suggested
            FROM imported_transactions
            WHERE household_id = @HouseholdId AND status = 'pending'
            """,
            new { HouseholdId = householdId, Min = bulkConfidence }, ct));
    }

    public async Task<int> CountPendingAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int>(Cmd(
            "SELECT count(*)::int FROM imported_transactions WHERE household_id = @HouseholdId AND status = 'pending'",
            new { HouseholdId = householdId }, ct));

    // ---------------------------------------------------------------------------------------------
    // Updates
    // ---------------------------------------------------------------------------------------------

    public Task<int> SetSuggestionAsync(NpgsqlConnection conn, Guid householdId, Guid id, Suggestion suggestion, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET suggested_category_id = @CategoryId, suggested_item_id = @ItemId, suggested_due_date = @DueDate,
                suggested_transfer = @Transfer, confidence = @Confidence
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                suggestion.CategoryId,
                suggestion.ItemId,
                suggestion.DueDate,
                Transfer = suggestion.Kind == SuggestionKind.Transfer,
                Confidence = (short)Math.Clamp(suggestion.Confidence, 0, 100),
            }, ct));

    public Task<int> ConfirmExpenseAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid expenseId, Guid? reviewer, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'confirmed', confirmed_expense_id = @ExpenseId, confirmed_item_id = NULL, confirmed_due_date = NULL,
                confirmed_auto = false, reviewed_by = @Reviewer, reviewed_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ExpenseId = expenseId, Reviewer = reviewer, Now = now }, ct));

    public Task<int> ConfirmOccurrenceAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid itemId, DateOnly dueDate, Guid? reviewer, bool auto, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'confirmed', confirmed_expense_id = NULL, confirmed_item_id = @ItemId, confirmed_due_date = @DueDate,
                confirmed_auto = @Auto, reviewed_by = @Reviewer, reviewed_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ItemId = itemId, DueDate = dueDate, Auto = auto, Reviewer = reviewer, Now = now }, ct));

    public Task<int> IgnoreAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid? reviewer, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'ignored', confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false,
                reviewed_by = @Reviewer, reviewed_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, Reviewer = reviewer, Now = now }, ct));

    public Task<int> RestoreAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'pending', confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false,
                reviewed_by = NULL, reviewed_at = NULL
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>When an external account is (un)mapped, its rows follow so the inbox shows the right account.</summary>
    public Task<int> SetAccountForExternalAsync(NpgsqlConnection conn, Guid householdId, Guid externalAccountId, Guid? accountId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "UPDATE imported_transactions SET account_id = @AccountId WHERE household_id = @HouseholdId AND external_account_id = @ExternalAccountId",
            new { HouseholdId = householdId, ExternalAccountId = externalAccountId, AccountId = accountId }, ct));

    // ---------------------------------------------------------------------------------------------
    // Payee rules
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PayeeRuleRow>> ListRulesAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<PayeeRuleRow>(Cmd(
            "SELECT household_id, payee_norm, category_id, item_id, hits, updated_at FROM payee_rules WHERE household_id = @HouseholdId",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>Learns or strengthens a rule: a category confirmation sets the category, an occurrence confirmation sets the item; the other half is kept.</summary>
    public Task<int> UpsertRuleAsync(NpgsqlConnection conn, Guid householdId, string payeeNorm, Guid? categoryId, Guid? itemId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO payee_rules (household_id, payee_norm, category_id, item_id, hits, updated_at)
            VALUES (@HouseholdId, @PayeeNorm, @CategoryId, @ItemId, 1, @Now)
            ON CONFLICT (household_id, payee_norm) DO UPDATE
            SET category_id = COALESCE(EXCLUDED.category_id, payee_rules.category_id),
                item_id = COALESCE(EXCLUDED.item_id, payee_rules.item_id),
                hits = payee_rules.hits + 1,
                updated_at = EXCLUDED.updated_at
            """,
            new { HouseholdId = householdId, PayeeNorm = payeeNorm, CategoryId = categoryId, ItemId = itemId, Now = now }, ct));

    /// <summary>
    /// v4 payee hint: points the rule for <paramref name="payeeNorm"/> at the item (hits stay as they are; a fresh rule starts at 1).
    /// Any learned category on the same payee is kept.
    /// </summary>
    public Task<int> SetItemRuleAsync(NpgsqlConnection conn, Guid householdId, string payeeNorm, Guid itemId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO payee_rules (household_id, payee_norm, category_id, item_id, hits, updated_at)
            VALUES (@HouseholdId, @PayeeNorm, NULL, @ItemId, 1, @Now)
            ON CONFLICT (household_id, payee_norm) DO UPDATE
            SET item_id = EXCLUDED.item_id, updated_at = EXCLUDED.updated_at
            """,
            new { HouseholdId = householdId, PayeeNorm = payeeNorm, ItemId = itemId, Now = now }, ct));

    /// <summary>
    /// Undoes <see cref="SetItemRuleAsync"/>: rules still pointing at <paramref name="itemId"/> (optionally only the one for
    /// <paramref name="payeeNorm"/>) are deleted, or merely unlinked from the item when they also carry a learned category.
    /// </summary>
    public async Task ClearItemRuleAsync(NpgsqlConnection conn, Guid householdId, string? payeeNorm, Guid itemId, CancellationToken ct)
    {
        var parameters = new { HouseholdId = householdId, PayeeNorm = payeeNorm, ItemId = itemId };
        await conn.ExecuteAsync(Cmd(
            """
            DELETE FROM payee_rules
            WHERE household_id = @HouseholdId AND item_id = @ItemId AND category_id IS NULL
              AND (@PayeeNorm::text IS NULL OR payee_norm = @PayeeNorm)
            """, parameters, ct));
        await conn.ExecuteAsync(Cmd(
            """
            UPDATE payee_rules SET item_id = NULL
            WHERE household_id = @HouseholdId AND item_id = @ItemId
              AND (@PayeeNorm::text IS NULL OR payee_norm = @PayeeNorm)
            """, parameters, ct));
    }

    // ---------------------------------------------------------------------------------------------
    // CSV imports
    // ---------------------------------------------------------------------------------------------

    public Task<CsvImportRow> InsertCsvImportAsync(NpgsqlConnection conn, Guid householdId, Guid userId, Guid accountId, string filename, int rowCount, int imported, int duplicates, DateTime now, CancellationToken ct) =>
        conn.QuerySingleAsync<CsvImportRow>(Cmd(
            """
            INSERT INTO csv_imports (id, household_id, account_id, filename, row_count, imported_count, duplicate_count, created_by, created_at)
            VALUES (@Id, @HouseholdId, @AccountId, @Filename, @RowCount, @Imported, @Duplicates, @UserId, @Now)
            RETURNING id, household_id, account_id, filename, row_count, imported_count, duplicate_count, created_by, created_at
            """,
            new { Id = Guid.CreateVersion7(), HouseholdId = householdId, AccountId = accountId, Filename = filename, RowCount = rowCount, Imported = imported, Duplicates = duplicates, UserId = userId, Now = now }, ct));

    public async Task<IReadOnlyList<CsvImportRow>> ListCsvImportsAsync(Guid householdId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CsvImportRow>(Cmd(
            """
            SELECT c.id, c.household_id, c.account_id, c.filename, c.row_count, c.imported_count, c.duplicate_count, c.created_by, c.created_at,
                   u.display_name AS created_by_name
            FROM csv_imports c
            LEFT JOIN users u ON u.id = c.created_by
            WHERE c.household_id = @HouseholdId
            ORDER BY c.created_at DESC, c.id DESC
            LIMIT @Limit
            """,
            new { HouseholdId = householdId, Limit = limit }, ct));
        return rows.ToList();
    }
}
