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
    bool Pending,
    string Status = "pending");

/// <summary>
/// v5 per-account transaction list. <paramref name="Filter"/>: all | pending | review | matched | transfers (validated by the endpoint).
/// <paramref name="From"/>/<paramref name="To"/> bound <c>posted_on</c> (a month). Hidden rows (dropped/superseded) only with filter all.
/// </summary>
public sealed record AccountTransactionQuery(Guid? AccountId, string Filter, string? Search, DateOnly? From, DateOnly? To, bool IncludeHidden);

public sealed record AccountTransactionTotals(long InCents, long OutCents, long PendingOutCents, int Count);

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
               t.reviewed_by, t.reviewed_at, t.created_at, t.ignore_reason, t.transfer_peer_id, t.transfer_auto, t.last_seen_at,
               a.name AS account_name, c.name AS suggested_category_name, i.name AS suggested_item_name, i.kind AS suggested_item_kind,
               u.display_name AS reviewed_by_name, cc.name AS confirmed_category_name, ci.name AS confirmed_item_name,
               ci.kind AS confirmed_item_kind, ex.category_id AS confirmed_category_id, pt.account_id AS peer_account_id, pa.name AS peer_account_name,
               t.match_score, t.match_detail::text AS match_detail, t.auto_blocked,
               'bank' AS kind
        FROM imported_transactions t
        LEFT JOIN accounts a ON a.id = t.account_id
        LEFT JOIN categories c ON c.id = t.suggested_category_id
        LEFT JOIN recurring_items i ON i.id = t.suggested_item_id
        LEFT JOIN users u ON u.id = t.reviewed_by
        LEFT JOIN expenses ex ON ex.id = t.confirmed_expense_id
        LEFT JOIN categories cc ON cc.id = ex.category_id
        LEFT JOIN recurring_items ci ON ci.id = t.confirmed_item_id
        LEFT JOIN imported_transactions pt ON pt.id = t.transfer_peer_id AND pt.household_id = t.household_id
        LEFT JOIN accounts pa ON pa.id = pt.account_id
        """;

    /// <summary>
    /// A manual expense shaped like <see cref="Select"/> (same column order) for the per-account transaction list: id = expense id,
    /// source/kind 'manual', amount negative (money out), status confirmed.
    /// </summary>
    private const string SelectManual =
        """
        SELECT e.id, e.household_id, 'manual'::text AS source, NULL::uuid AS external_account_id, e.account_id, NULL::text AS external_id,
               e.spent_on AS posted_on, -e.amount_cents AS amount_cents, e.name AS description, NULL::text AS payee, e.note AS memo,
               false AS pending, 'confirmed'::text AS status, NULL::uuid AS suggested_category_id, NULL::uuid AS suggested_item_id,
               NULL::date AS suggested_due_date, false AS suggested_transfer, 0::smallint AS confidence, e.id AS confirmed_expense_id,
               NULL::uuid AS confirmed_item_id, NULL::date AS confirmed_due_date, false AS confirmed_auto,
               e.created_by AS reviewed_by, e.created_at AS reviewed_at, e.created_at, NULL::text AS ignore_reason, NULL::uuid AS transfer_peer_id,
               false AS transfer_auto, NULL::timestamptz AS last_seen_at,
               a.name AS account_name, NULL::text AS suggested_category_name, NULL::text AS suggested_item_name, NULL::text AS suggested_item_kind,
               u.display_name AS reviewed_by_name, cc.name AS confirmed_category_name, NULL::text AS confirmed_item_name,
               NULL::text AS confirmed_item_kind, e.category_id AS confirmed_category_id, NULL::uuid AS peer_account_id, NULL::text AS peer_account_name,
               NULL::smallint AS match_score, NULL::text AS match_detail, false AS auto_blocked,
               'manual' AS kind
        FROM expenses e
        LEFT JOIN accounts a ON a.id = e.account_id
        LEFT JOIN users u ON u.id = e.created_by
        LEFT JOIN categories cc ON cc.id = e.category_id
        """;

    /// <summary>v7: a row that pays an occurrence — confirmed against it, or a transfer row linked to a planned transfer.</summary>
    private const string PaysOccurrence = "(t.status = 'confirmed' OR (t.status = 'ignored' AND t.ignore_reason = 'transfer'))";

    /// <summary>Rows hidden from the inbox and account lists unless asked for (v5).</summary>
    private const string NotHidden = "(t.ignore_reason IS NULL OR t.ignore_reason NOT IN ('dropped', 'superseded'))";

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
                    @AmountCents, @Description, @Payee, @Memo, @Pending, @Status, @Now)
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
                t.Status,
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
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListAsync(Guid householdId, string? status, InboxCursor? before, int limit, CancellationToken ct, bool includeHidden = false)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId
              AND (@Status::text IS NULL OR t.status = @Status)
              AND (@IncludeHidden OR {NotHidden})
              AND (@BeforeDate::date IS NULL OR (t.posted_on, t.id) < (@BeforeDate, @BeforeId))
            ORDER BY t.posted_on DESC, t.id DESC
            LIMIT @Limit
            """,
            new { HouseholdId = householdId, Status = status, IncludeHidden = includeHidden, BeforeDate = before?.PostedOn, BeforeId = before?.Id ?? Guid.Empty, Limit = limit + 1 }, ct));
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

    /// <summary>Confirms a row as an expense. <paramref name="auto"/> (v6): auto-categorised by the matcher (<c>reviewer</c> null).</summary>
    public Task<int> ConfirmExpenseAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid expenseId, Guid? reviewer, DateTime now, CancellationToken ct, bool auto = false) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'confirmed', confirmed_expense_id = @ExpenseId, confirmed_item_id = NULL, confirmed_due_date = NULL,
                confirmed_auto = @Auto, reviewed_by = @Reviewer, reviewed_at = @Now,
                ignore_reason = NULL, transfer_peer_id = NULL, transfer_auto = false
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ExpenseId = expenseId, Reviewer = reviewer, Now = now, Auto = auto }, ct));

    /// <summary>
    /// v6: stores what the matcher (or the built-in fallback) suggests for a row still to review, with its score and detail
    /// (<paramref name="detailJson"/> = <c>{ name, amount, date, reason, engine }</c>). Rows reviewed in the meantime are left alone.
    /// </summary>
    public Task<int> SetMatchAsync(NpgsqlConnection conn, Guid householdId, Guid id, Suggestion suggestion, int? score, string? detailJson, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET suggested_category_id = @CategoryId, suggested_item_id = @ItemId, suggested_due_date = @DueDate,
                suggested_transfer = @Transfer, confidence = @Confidence, match_score = @Score, match_detail = @Detail::jsonb
            WHERE id = @Id AND household_id = @HouseholdId AND status = 'pending'
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
                Score = score is { } v ? (short?)Math.Clamp(v, 0, 100) : null,
                Detail = detailJson,
            }, ct));

    /// <summary>v6: a person sent the row back to review — automatic matching must never apply it again.</summary>
    public Task<int> BlockAutoAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "UPDATE imported_transactions SET auto_blocked = true WHERE id = @Id AND household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId }, ct));

    public Task<int> ConfirmOccurrenceAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid itemId, DateOnly dueDate, Guid? reviewer, bool auto, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'confirmed', confirmed_expense_id = NULL, confirmed_item_id = @ItemId, confirmed_due_date = @DueDate,
                confirmed_auto = @Auto, reviewed_by = @Reviewer, reviewed_at = @Now,
                ignore_reason = NULL, transfer_peer_id = NULL, transfer_auto = false
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ItemId = itemId, DueDate = dueDate, Auto = auto, Reviewer = reviewer, Now = now }, ct));

    /// <summary>Ignores a row. <paramref name="reason"/> (v5): manual | transfer | dropped | superseded. Never touches the transfer peer columns.</summary>
    public Task<int> IgnoreAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid? reviewer, DateTime now, CancellationToken ct, string reason = "manual") =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'ignored', confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false,
                reviewed_by = @Reviewer, reviewed_at = @Now, ignore_reason = @Reason,
                transfer_peer_id = CASE WHEN @Reason = 'transfer' THEN transfer_peer_id END,
                transfer_auto = CASE WHEN @Reason = 'transfer' THEN transfer_auto ELSE false END
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, Reviewer = reviewer, Now = now, Reason = reason }, ct));

    /// <summary>v5: marks a row as a transfer (ignored, reason transfer) with its peer (null = one-sided).</summary>
    public Task<int> MarkTransferAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid? peerId, bool auto, Guid? reviewer, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'ignored', ignore_reason = 'transfer', transfer_peer_id = @PeerId, transfer_auto = @Auto,
                confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false,
                reviewed_by = @Reviewer, reviewed_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, PeerId = peerId, Auto = auto, Reviewer = reviewer, Now = now }, ct));

    /// <summary>
    /// Back to review with every confirmation cleared. v7: a row on a <c>fun</c> account never goes to To review — it returns to
    /// <c>confirmed</c> (nothing attached) instead, so every restore / unmatch / unpair path keeps personal fun money out of the inbox.
    /// </summary>
    public Task<int> RestoreAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = CASE WHEN EXISTS (SELECT 1 FROM accounts fa WHERE fa.id = imported_transactions.account_id AND fa.role = 'fun')
                              THEN 'confirmed' ELSE 'pending' END,
                confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false,
                reviewed_by = NULL, reviewed_at = NULL, ignore_reason = NULL, transfer_peer_id = NULL, transfer_auto = false
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>When an external account is (un)mapped, its rows follow so the inbox shows the right account.</summary>
    public Task<int> SetAccountForExternalAsync(NpgsqlConnection conn, Guid householdId, Guid externalAccountId, Guid? accountId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "UPDATE imported_transactions SET account_id = @AccountId WHERE household_id = @HouseholdId AND external_account_id = @ExternalAccountId",
            new { HouseholdId = householdId, ExternalAccountId = externalAccountId, AccountId = accountId }, ct));

    // ---------------------------------------------------------------------------------------------
    // v5: pending lifecycle (reconciliation)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows the reconciler needs for one external account: every row whose fingerprint is in this response (same id), plus the account's
    /// bank-pending rows posted on or after <paramref name="pendingSince"/> (candidates for replacement / dropping).
    /// </summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListForReconcileAsync(NpgsqlConnection conn, Guid householdId, Guid externalAccountId, IReadOnlyCollection<byte[]> fingerprints, DateOnly pendingSince, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId
              AND (t.fingerprint = ANY(@Fingerprints) OR (t.external_account_id = @ExternalAccountId AND t.pending AND t.posted_on >= @Since))
            ORDER BY t.created_at, t.id
            """,
            new { HouseholdId = householdId, ExternalAccountId = externalAccountId, Fingerprints = fingerprints.ToArray(), Since = pendingSince }, ct));
        return rows.ToList();
    }

    /// <summary>
    /// The posted version of a row: amount, date, description, payee, memo and <c>pending = false</c>. A replacement (new posted id) also
    /// moves the row onto the new external id + fingerprint. Id, status, suggestion and confirmation are kept.
    /// </summary>
    public Task<int> ApplyPostedAsync(NpgsqlConnection conn, Guid householdId, Guid id, string? externalId, byte[]? fingerprint, DateOnly postedOn, long amountCents, string description, string? payee, string? memo, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET pending = false, posted_on = @PostedOn, amount_cents = @AmountCents, description = @Description, payee = @Payee, memo = @Memo,
                external_id = COALESCE(@ExternalId, external_id), fingerprint = COALESCE(@Fingerprint, fingerprint), last_seen_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ExternalId = externalId, Fingerprint = fingerprint, PostedOn = postedOn, AmountCents = amountCents, Description = description, Payee = payee, Memo = memo, Now = now }, ct));

    /// <summary>Hides a row as dropped / superseded (ignored, no confirmation, no peer). The caller reverses the confirmation first.</summary>
    public Task<int> HideAsync(NpgsqlConnection conn, Guid householdId, Guid id, string reason, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'ignored', ignore_reason = @Reason, confirmed_expense_id = NULL, confirmed_item_id = NULL, confirmed_due_date = NULL,
                confirmed_auto = false, transfer_peer_id = NULL, transfer_auto = false, reviewed_by = NULL, reviewed_at = @Now
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, Reason = reason, Now = now }, ct));

    /// <summary>Every row present in a Bridge response gets <c>last_seen_at</c>.</summary>
    public Task<int> TouchSeenAsync(NpgsqlConnection conn, Guid householdId, IReadOnlyCollection<byte[]> fingerprints, DateTime now, CancellationToken ct) =>
        fingerprints.Count == 0
            ? Task.FromResult(0)
            : conn.ExecuteAsync(Cmd(
                "UPDATE imported_transactions SET last_seen_at = @Now WHERE household_id = @HouseholdId AND fingerprint = ANY(@Fingerprints)",
                new { HouseholdId = householdId, Fingerprints = fingerprints.ToArray(), Now = now }, ct));

    // ---------------------------------------------------------------------------------------------
    // v5: transfers and candidates
    // ---------------------------------------------------------------------------------------------

    /// <summary>Visible rows posted within [from, to] (transfer peers, occurrence candidates). Ranking happens in the caller.</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListVisibleInWindowAsync(NpgsqlConnection conn, Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.household_id = @HouseholdId AND t.posted_on BETWEEN @From AND @To AND {NotHidden} ORDER BY t.posted_on, t.id",
            new { HouseholdId = householdId, From = from, To = to }, ct));
        return rows.ToList();
    }

    /// <summary>Rows confirmed against any occurrence of <paramref name="itemId"/> (bill payments view).</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListConfirmedForItemAsync(NpgsqlConnection conn, Guid householdId, Guid itemId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"{Select} WHERE t.household_id = @HouseholdId AND {PaysOccurrence} AND t.confirmed_item_id = @ItemId ORDER BY t.confirmed_due_date DESC, t.id DESC",
            new { HouseholdId = householdId, ItemId = itemId }, ct));
        return rows.ToList();
    }

    /// <summary>Rows ignored as transfers posted within [from, to] (the transfer report).</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListTransfersAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId AND t.status = 'ignored' AND t.ignore_reason = 'transfer' AND t.posted_on BETWEEN @From AND @To
            ORDER BY t.posted_on DESC, t.id DESC
            """,
            new { HouseholdId = householdId, From = from, To = to }, ct));
        var list = rows.ToList();

        // Peers outside the month still make the pair.
        var missing = list.Select(r => r.TransferPeerId).OfType<Guid>().Where(id => list.All(r => r.Id != id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            list.AddRange(await ListByIdsAsync(conn, householdId, missing, ct));
        }

        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // v5: per-account transactions (bank rows ∪ manual expenses on the account)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Newest first by (posted_on, id); returns one row more than <paramref name="limit"/> for the cursor.</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListAccountTransactionsAsync(Guid householdId, AccountTransactionQuery query, InboxCursor? before, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            SELECT * FROM ({AccountUnion(query)}) u
            WHERE (@BeforeDate::date IS NULL OR (u.posted_on, u.id) < (@BeforeDate, @BeforeId))
            ORDER BY u.posted_on DESC, u.id DESC
            LIMIT @Limit
            """,
            AccountParameters(householdId, query, before, limit + 1), ct));
        return rows.ToList();
    }

    /// <summary>Totals over the whole filtered set (not just the page): money in, money out, still-pending outflow, row count.</summary>
    public async Task<AccountTransactionTotals> AccountTransactionTotalsAsync(Guid householdId, AccountTransactionQuery query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<AccountTransactionTotals>(Cmd(
            $"""
            SELECT COALESCE(sum(u.amount_cents) FILTER (WHERE u.amount_cents > 0), 0)::bigint AS in_cents,
                   COALESCE(sum(-u.amount_cents) FILTER (WHERE u.amount_cents < 0), 0)::bigint AS out_cents,
                   COALESCE(sum(-u.amount_cents) FILTER (WHERE u.amount_cents < 0 AND u.pending AND (u.ignore_reason IS NULL OR u.ignore_reason NOT IN ('dropped', 'superseded'))), 0)::bigint AS pending_out_cents,
                   count(*)::int AS count
            FROM ({AccountUnion(query)}) u
            """,
            AccountParameters(householdId, query, null, 0), ct));
    }

    /// <summary>The filter fragments come from a fixed whitelist; user input (search, dates, ids) is always a parameter.</summary>
    private static string AccountUnion(AccountTransactionQuery query)
    {
        var bank = new List<string> { "t.household_id = @HouseholdId" };
        var manual = new List<string>
        {
            "e.household_id = @HouseholdId",
            "NOT EXISTS (SELECT 1 FROM imported_transactions x WHERE x.household_id = @HouseholdId AND x.confirmed_expense_id = e.id)",
        };
        if (query.AccountId is not null)
        {
            bank.Add("t.account_id = @AccountId");
            manual.Add("e.account_id = @AccountId");
        }

        // v6 "posted" (the Transactions feed): every bank row that is no longer pending at the bank, plus manual expenses.
        var includeManual = query.Filter is "all" or "matched" or "posted";

        switch (query.Filter)
        {
            case "pending":
                bank.Add("t.pending");
                break;
            case "posted":
                bank.Add("NOT t.pending");
                break;
            case "review":
                bank.Add("t.status = 'pending'");
                break;
            case "matched":
                bank.Add("t.status = 'confirmed'");
                break;
            case "transfers":
                bank.Add("t.status = 'ignored' AND t.ignore_reason = 'transfer'");
                break;
        }

        if (!(query.Filter == "all" && query.IncludeHidden))
        {
            bank.Add(NotHidden);
        }

        if (query.From is not null)
        {
            bank.Add("t.posted_on BETWEEN @From AND @To");
            manual.Add("e.spent_on BETWEEN @From AND @To");
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            bank.Add(@"(t.description ILIKE @Pattern ESCAPE '\' OR t.payee ILIKE @Pattern ESCAPE '\' OR t.memo ILIKE @Pattern ESCAPE '\')");
            manual.Add(@"(e.name ILIKE @Pattern ESCAPE '\' OR e.note ILIKE @Pattern ESCAPE '\')");
        }

        var sql = $"{Select} WHERE {string.Join(" AND ", bank)}";
        return includeManual ? $"{sql} UNION ALL {SelectManual} WHERE {string.Join(" AND ", manual)}" : sql;
    }

    private static object AccountParameters(Guid householdId, AccountTransactionQuery query, InboxCursor? before, int limit) => new
    {
        HouseholdId = householdId,
        AccountId = query.AccountId ?? Guid.Empty,
        From = query.From ?? DateOnly.MinValue,
        To = query.To ?? DateOnly.MaxValue,
        Pattern = string.IsNullOrWhiteSpace(query.Search) ? "" : $"%{EscapeLike(query.Search.Trim())}%",
        BeforeDate = before?.PostedOn,
        BeforeId = before?.Id ?? Guid.Empty,
        Limit = limit,
    };

    /// <summary>Escapes LIKE wildcards so a search for "50%" matches the text "50%".</summary>
    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------------
    // v6: Transactions feed, pending view, matcher sweeps, found bills
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every visible row still pending at the bank (optionally one account / a search), newest first. No paging: it is short.</summary>
    public async Task<IReadOnlyList<ImportedTransactionRow>> ListBankPendingAsync(Guid householdId, Guid? accountId, string? search, bool reviewOrConfirmedOnly, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var query = new AccountTransactionQuery(accountId, "pending", search, null, null, false);
        var statusFilter = reviewOrConfirmedOnly ? "AND u.status IN ('pending', 'confirmed')" : "";
        var rows = await conn.QueryAsync<ImportedTransactionRow>(Cmd(
            $"""
            SELECT * FROM ({AccountUnion(query)}) u
            WHERE true {statusFilter}
            ORDER BY u.posted_on DESC, u.id DESC
            LIMIT 1000
            """,
            AccountParameters(householdId, query, null, 0), ct));
        return rows.ToList();
    }

    /// <summary>Households with at least one row to review (the scheduler's rematch sweep).</summary>
    public async Task<IReadOnlyList<Guid>> HouseholdsWithPendingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(Cmd(
            "SELECT DISTINCT household_id FROM imported_transactions WHERE status = 'pending' ORDER BY household_id", null, ct));
        return rows.ToList();
    }

    /// <summary>Households with imported transactions posted on or after <paramref name="since"/> (daily found-bills detection).</summary>
    public async Task<IReadOnlyList<Guid>> HouseholdsWithImportsAsync(DateOnly since, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(Cmd(
            "SELECT DISTINCT household_id FROM imported_transactions WHERE posted_on >= @Since ORDER BY household_id", new { Since = since }, ct));
        return rows.ToList();
    }

    /// <summary>
    /// Found-bill input: rows posted on or after <paramref name="since"/>, without transfers and hidden (dropped / superseded) rows,
    /// newest first, at most <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<DetectionRow>> ListForDetectionAsync(NpgsqlConnection conn, Guid householdId, DateOnly since, int limit, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<DetectionRow>(Cmd(
            """
            SELECT t.id, t.posted_on, t.amount_cents, t.description, t.payee
            FROM imported_transactions t
            WHERE t.household_id = @HouseholdId AND t.posted_on >= @Since AND t.amount_cents <> 0
              AND (t.ignore_reason IS NULL OR t.ignore_reason NOT IN ('transfer', 'dropped', 'superseded'))
            ORDER BY t.posted_on DESC, t.id DESC
            LIMIT @Limit
            """,
            new { HouseholdId = householdId, Since = since, Limit = limit }, ct));
        return rows.ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // v7: fun accounts, planned transfers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// v7: an account became <c>fun</c> (or a bank account was mapped onto one): its rows still to review become <c>confirmed</c> with no
    /// suggestion, so personal fun money never waits in To review. Returns how many moved.
    /// </summary>
    public Task<int> ConfirmFunPendingAsync(NpgsqlConnection conn, Guid householdId, Guid accountId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions
            SET status = 'confirmed', suggested_category_id = NULL, suggested_item_id = NULL, suggested_due_date = NULL,
                suggested_transfer = false, confidence = 0, match_score = NULL, match_detail = NULL
            WHERE household_id = @HouseholdId AND account_id = @AccountId AND status = 'pending'
            """,
            new { HouseholdId = householdId, AccountId = accountId }, ct));

    /// <summary>v7: a transfer row (ignored / transfer) records the planned-transfer occurrence it paid.</summary>
    public Task<int> SetPlannedLinkAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid itemId, DateOnly dueDate, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            UPDATE imported_transactions SET confirmed_item_id = @ItemId, confirmed_due_date = @DueDate, confirmed_auto = true
            WHERE id = @Id AND household_id = @HouseholdId
            """,
            new { Id = id, HouseholdId = householdId, ItemId = itemId, DueDate = dueDate }, ct));

    /// <summary>
    /// v7: the planned transfer occurrence was marked unpaid by hand — the transfer rows that paid it stay transfers but lose the link and
    /// are blocked from automatic matching (an Undo must stick). Returns the unlinked row ids.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> UnlinkPlannedAsync(NpgsqlConnection conn, Guid householdId, Guid itemId, DateOnly dueDate, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Guid>(Cmd(
            """
            UPDATE imported_transactions SET confirmed_item_id = NULL, confirmed_due_date = NULL, confirmed_auto = false, auto_blocked = true
            WHERE household_id = @HouseholdId AND status = 'ignored' AND ignore_reason = 'transfer'
              AND confirmed_item_id = @ItemId AND confirmed_due_date = @DueDate
            RETURNING id
            """,
            new { HouseholdId = householdId, ItemId = itemId, DueDate = dueDate }, ct));
        return rows.ToList();
    }

    /// <summary>v7: a one-sided transfer gets its other leg (the inflow arrived after the outflow was matched).</summary>
    public Task<int> SetTransferPeerAsync(NpgsqlConnection conn, Guid householdId, Guid id, Guid peerId, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "UPDATE imported_transactions SET transfer_peer_id = @PeerId WHERE id = @Id AND household_id = @HouseholdId AND status = 'ignored' AND ignore_reason = 'transfer'",
            new { Id = id, HouseholdId = householdId, PeerId = peerId }, ct));

    /// <summary>The row that pays an occurrence: confirmed against it or (v7) a transfer linked to it. Null when none.</summary>
    public Task<ImportedTransactionRow?> FindPayingRowAsync(NpgsqlConnection conn, Guid householdId, Guid itemId, DateOnly dueDate, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<ImportedTransactionRow>(Cmd(
            $"""
            {Select}
            WHERE t.household_id = @HouseholdId AND {PaysOccurrence} AND t.confirmed_item_id = @ItemId AND t.confirmed_due_date = @DueDate
            ORDER BY (t.amount_cents < 0) DESC, t.reviewed_at DESC NULLS LAST, t.id DESC
            LIMIT 1
            """,
            new { HouseholdId = householdId, ItemId = itemId, DueDate = dueDate }, ct));

    // ---------------------------------------------------------------------------------------------
    // Payee rules
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PayeeRuleRow>> ListRulesAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<PayeeRuleRow>(Cmd(
            "SELECT household_id, payee_norm, category_id, item_id, hits, is_transfer, updated_at FROM payee_rules WHERE household_id = @HouseholdId",
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>
    /// Learns or strengthens a rule: a category confirmation sets the category, an occurrence confirmation sets the item; the other half is
    /// kept. Either explicit choice clears a learned transfer flag (v5: the last explicit choice wins).
    /// </summary>
    public Task<int> UpsertRuleAsync(NpgsqlConnection conn, Guid householdId, string payeeNorm, Guid? categoryId, Guid? itemId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO payee_rules (household_id, payee_norm, category_id, item_id, hits, updated_at)
            VALUES (@HouseholdId, @PayeeNorm, @CategoryId, @ItemId, 1, @Now)
            ON CONFLICT (household_id, payee_norm) DO UPDATE
            SET category_id = COALESCE(EXCLUDED.category_id, payee_rules.category_id),
                item_id = COALESCE(EXCLUDED.item_id, payee_rules.item_id),
                hits = payee_rules.hits + 1,
                is_transfer = false,
                updated_at = EXCLUDED.updated_at
            """,
            new { HouseholdId = householdId, PayeeNorm = payeeNorm, CategoryId = categoryId, ItemId = itemId, Now = now }, ct));

    /// <summary>v5: "this payee is a transfer" (one-sided Mark as transfer). Keeps any learned category/item but they lose to the flag.</summary>
    public Task<int> SetTransferRuleAsync(NpgsqlConnection conn, Guid householdId, string payeeNorm, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO payee_rules (household_id, payee_norm, category_id, item_id, hits, is_transfer, updated_at)
            VALUES (@HouseholdId, @PayeeNorm, NULL, NULL, 1, true, @Now)
            ON CONFLICT (household_id, payee_norm) DO UPDATE
            SET is_transfer = true, hits = payee_rules.hits + 1, updated_at = EXCLUDED.updated_at
            """,
            new { HouseholdId = householdId, PayeeNorm = payeeNorm, Now = now }, ct));

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
            SET item_id = EXCLUDED.item_id, is_transfer = false, updated_at = EXCLUDED.updated_at
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
