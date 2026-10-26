using CouplesCash.Api.Domain;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>v6 <c>bill_suggestions</c> (found bills). Every statement is scoped by <c>household_id</c>.</summary>
public sealed class BillSuggestionRepository(NpgsqlDataSource db)
{
    private const string Columns =
        """
        id, household_id, name, kind, amount_cents, amount_varies, frequency, next_due, bank_name, sample_transaction_ids, last_seen,
        score, status, item_id, fingerprint, created_at, updated_at
        """;

    /// <summary>Open suggestions, best first.</summary>
    public async Task<IReadOnlyList<BillSuggestionRow>> ListOpenAsync(Guid householdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BillSuggestionRow>(Cmd(
            $"""
            SELECT {Columns} FROM bill_suggestions
            WHERE household_id = @HouseholdId AND status = 'open'
            ORDER BY score DESC, last_seen DESC NULLS LAST, name, id
            """,
            new { HouseholdId = householdId }, ct));
        return rows.ToList();
    }

    /// <summary>Every stored fingerprint with its status (the detection upsert plan).</summary>
    public async Task<IReadOnlyList<StoredBillSuggestion>> ListStoredAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<BillSuggestionRow>(Cmd(
            $"SELECT {Columns} FROM bill_suggestions WHERE household_id = @HouseholdId",
            new { HouseholdId = householdId }, ct));
        return rows.Select(r => new StoredBillSuggestion(r.Id, r.Fingerprint, r.Status)).ToList();
    }

    /// <summary>One suggestion, row-locked for accept / dismiss.</summary>
    public Task<BillSuggestionRow?> GetForUpdateAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<BillSuggestionRow>(Cmd(
            $"SELECT {Columns} FROM bill_suggestions WHERE id = @Id AND household_id = @HouseholdId FOR UPDATE",
            new { Id = id, HouseholdId = householdId }, ct));

    /// <summary>Inserts a new open suggestion or refreshes an open one with the same fingerprint (dismissed / accepted rows are never touched).</summary>
    public Task<int> UpsertOpenAsync(NpgsqlConnection conn, Guid householdId, DetectedBill bill, DateOnly? lastSeen, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO bill_suggestions (id, household_id, name, kind, amount_cents, amount_varies, frequency, next_due, bank_name,
                                          sample_transaction_ids, last_seen, score, status, fingerprint, created_at, updated_at)
            VALUES (@Id, @HouseholdId, @Name, @Kind, @AmountCents, @AmountVaries, @Frequency, @NextDue, @BankName,
                    @Samples, @LastSeen, @Score, 'open', @Fingerprint, @Now, @Now)
            ON CONFLICT (household_id, fingerprint) DO UPDATE
            SET name = EXCLUDED.name, kind = EXCLUDED.kind, amount_cents = EXCLUDED.amount_cents, amount_varies = EXCLUDED.amount_varies,
                next_due = EXCLUDED.next_due, bank_name = EXCLUDED.bank_name, sample_transaction_ids = EXCLUDED.sample_transaction_ids,
                last_seen = EXCLUDED.last_seen, score = EXCLUDED.score, updated_at = EXCLUDED.updated_at
            WHERE bill_suggestions.status = 'open'
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                HouseholdId = householdId,
                bill.Name,
                Kind = bill.Kind.ToDb(),
                bill.AmountCents,
                bill.AmountVaries,
                Frequency = bill.Frequency.ToDb(),
                bill.NextDue,
                bill.BankName,
                Samples = bill.TransactionIds.ToArray(),
                LastSeen = lastSeen,
                Score = (short)Math.Clamp(bill.Score, 0, 100),
                bill.Fingerprint,
                Now = now,
            }, ct));

    /// <summary>Removes open suggestions the latest detection no longer reports.</summary>
    public Task<int> DeleteOpenAsync(NpgsqlConnection conn, Guid householdId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? Task.FromResult(0)
            : conn.ExecuteAsync(Cmd(
                "DELETE FROM bill_suggestions WHERE household_id = @HouseholdId AND status = 'open' AND id = ANY(@Ids)",
                new { HouseholdId = householdId, Ids = ids.ToArray() }, ct));

    public Task<int> SetStatusAsync(NpgsqlConnection conn, Guid householdId, Guid id, string status, Guid? itemId, DateTime now, CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            "UPDATE bill_suggestions SET status = @Status, item_id = COALESCE(@ItemId, item_id), updated_at = @Now WHERE id = @Id AND household_id = @HouseholdId",
            new { Id = id, HouseholdId = householdId, Status = status, ItemId = itemId, Now = now }, ct));
}
