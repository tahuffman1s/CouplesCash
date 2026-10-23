using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

/// <summary>Passkey credentials and the short-lived, single-use challenges.</summary>
public sealed class WebAuthnRepository(NpgsqlDataSource db)
{
    private const string CredentialColumns = "id, user_id, credential_id, public_key, sign_count, aaguid, transports, name, backed_up, created_at, last_used_at";

    public async Task<IReadOnlyList<WebAuthnCredentialRow>> ListForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<WebAuthnCredentialRow>(Cmd(
            $"SELECT {CredentialColumns} FROM webauthn_credentials WHERE user_id = @UserId ORDER BY created_at, id",
            new { UserId = userId }, ct));
        return rows.ToList();
    }

    public async Task<WebAuthnCredentialRow?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WebAuthnCredentialRow>(Cmd(
            $"SELECT {CredentialColumns} FROM webauthn_credentials WHERE credential_id = @CredentialId",
            new { CredentialId = credentialId }, ct));
    }

    public async Task<bool> CredentialIdExistsAsync(byte[] credentialId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(Cmd(
            "SELECT EXISTS (SELECT 1 FROM webauthn_credentials WHERE credential_id = @CredentialId)",
            new { CredentialId = credentialId }, ct));
    }

    public async Task<WebAuthnCredentialRow> InsertAsync(WebAuthnCredentialRow row, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<WebAuthnCredentialRow>(Cmd(
            $"""
            INSERT INTO webauthn_credentials (id, user_id, credential_id, public_key, sign_count, aaguid, transports, name, backed_up, created_at)
            VALUES (@Id, @UserId, @CredentialId, @PublicKey, @SignCount, @Aaguid, @Transports, @Name, @BackedUp, @CreatedAt)
            RETURNING {CredentialColumns}
            """,
            row, ct));
    }

    public async Task UpdateAfterLoginAsync(Guid id, long signCount, bool backedUp, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE webauthn_credentials SET sign_count = @SignCount, backed_up = @BackedUp, last_used_at = @Now WHERE id = @Id",
            new { Id = id, SignCount = signCount, BackedUp = backedUp, Now = now }, ct));
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(
            "DELETE FROM webauthn_credentials WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId }, ct));
        return rows > 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Challenges
    // ---------------------------------------------------------------------------------------------

    public async Task<Guid> InsertChallengeAsync(byte[] challenge, Guid? userId, string kind, string optionsJson, DateTime now, DateTime expiresAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO webauthn_challenges (id, challenge, user_id, kind, options, expires_at, created_at)
            VALUES (@Id, @Challenge, @UserId, @Kind, @Options::jsonb, @ExpiresAt, @Now)
            """,
            new { Id = id, Challenge = challenge, UserId = userId, Kind = kind, Options = optionsJson, ExpiresAt = expiresAt, Now = now }, ct));
        return id;
    }

    /// <summary>Deletes and returns the challenge in one step (single use). Null when unknown, expired, of another kind or another user.</summary>
    public async Task<WebAuthnChallengeRow?> ConsumeChallengeAsync(Guid id, string kind, Guid? userId, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WebAuthnChallengeRow>(Cmd(
            """
            DELETE FROM webauthn_challenges
            WHERE id = @Id AND kind = @Kind AND expires_at > @Now AND (user_id IS NOT DISTINCT FROM @UserId)
            RETURNING id, challenge, user_id, kind, options::text AS options, expires_at, created_at
            """,
            new { Id = id, Kind = kind, UserId = userId, Now = now }, ct));
    }

    public async Task<int> DeleteExpiredChallengesAsync(DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(Cmd("DELETE FROM webauthn_challenges WHERE expires_at <= @Now", new { Now = now }, ct));
    }
}
