using CouplesCash.Api.Auth;
using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class RefreshTokenRepository(NpgsqlDataSource db) : IRefreshTokenStore
{
    public async Task<RefreshTokenRecord?> FindAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RefreshTokenRecord>(Cmd(
            "SELECT id, user_id, token_hash, family_id, expires_at, revoked_at, user_agent, created_at FROM refresh_tokens WHERE token_hash = @TokenHash",
            new { TokenHash = tokenHash }, ct));
    }

    public async Task InsertAsync(RefreshTokenRecord record, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            """
            INSERT INTO refresh_tokens (id, user_id, token_hash, family_id, expires_at, revoked_at, user_agent, created_at)
            VALUES (@Id, @UserId, @TokenHash, @FamilyId, @ExpiresAt, @RevokedAt, @UserAgent, @CreatedAt)
            """,
            record, ct));
    }

    public async Task RevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, RevokedAt = revokedAt }, ct));
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTime revokedAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt WHERE family_id = @FamilyId AND revoked_at IS NULL",
            new { FamilyId = familyId, RevokedAt = revokedAt }, ct));
    }

    public async Task RevokeAllForUserAsync(Guid userId, DateTime revokedAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId, RevokedAt = revokedAt }, ct));
    }
}
