using CouplesCash.Api.Auth;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public enum RedeemStatus
{
    /// <summary>No live (unredeemed, unexpired) code matches.</summary>
    NotFound,

    /// <summary>The household already has two members.</summary>
    HouseholdFull,

    /// <summary>The caller is already a member of a household (lost a race with themselves).</summary>
    AlreadyMember,

    Redeemed,
}

public sealed record RedeemResult(RedeemStatus Status, Guid? HouseholdId);

public sealed class CoupleCodeRepository(NpgsqlDataSource db)
{
    /// <summary>Stores a new code and expires every earlier unredeemed code of the household.</summary>
    public async Task CreateAsync(Guid householdId, Guid createdBy, GeneratedCoupleCode code, DateTime now, DateTime expiresAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(Cmd(
            "UPDATE couple_codes SET expires_at = @Now WHERE household_id = @HouseholdId AND redeemed_at IS NULL AND expires_at > @Now",
            new { HouseholdId = householdId, Now = now }, ct));

        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO couple_codes (id, household_id, created_by, code_hash, payload_hash, expires_at, created_at)
            VALUES (@Id, @HouseholdId, @CreatedBy, @CodeHash, @PayloadHash, @ExpiresAt, @Now)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                HouseholdId = householdId,
                CreatedBy = createdBy,
                code.CodeHash,
                code.PayloadHash,
                ExpiresAt = expiresAt,
                Now = now,
            }, ct));

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Atomically redeems a code: locks it, adds the caller to the household (the SQL trigger rejects a
    /// third member) and marks the code used. <paramref name="lookupHash"/> is either sha256(payload bytes)
    /// or sha256(typed code); both columns are checked.
    /// </summary>
    public async Task<RedeemResult> RedeemAsync(byte[] lookupHash, Guid userId, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var code = await conn.QuerySingleOrDefaultAsync<CoupleCodeRow>(Cmd(
            """
            SELECT id, household_id, created_by, expires_at
            FROM couple_codes
            WHERE (code_hash = @Hash OR payload_hash = @Hash)
              AND redeemed_at IS NULL
              AND expires_at > @Now
            FOR UPDATE
            """,
            new { Hash = lookupHash, Now = now }, ct));

        if (code is null)
        {
            return new RedeemResult(RedeemStatus.NotFound, null);
        }

        try
        {
            await conn.ExecuteAsync(Cmd(
                "INSERT INTO household_members (household_id, user_id, joined_at) VALUES (@HouseholdId, @UserId, @Now)",
                new { code.HouseholdId, UserId = userId, Now = now }, ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.MessageText == "household_full")
        {
            return new RedeemResult(RedeemStatus.HouseholdFull, null);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new RedeemResult(RedeemStatus.AlreadyMember, null);
        }

        await conn.ExecuteAsync(Cmd(
            "UPDATE couple_codes SET redeemed_at = @Now, redeemed_by = @UserId WHERE id = @Id",
            new { code.Id, UserId = userId, Now = now }, ct));

        await tx.CommitAsync(ct);
        return new RedeemResult(RedeemStatus.Redeemed, code.HouseholdId);
    }
}
