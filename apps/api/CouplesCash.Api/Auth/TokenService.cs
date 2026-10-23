using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CouplesCash.Api.Auth;

/// <summary>A refresh token as stored: only the SHA-256 of the opaque token is kept.</summary>
public sealed class RefreshTokenRecord
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public byte[] TokenHash { get; init; } = [];

    public Guid FamilyId { get; init; }

    public DateTime ExpiresAt { get; init; }

    public DateTime? RevokedAt { get; init; }

    public string? UserAgent { get; init; }

    public DateTime CreatedAt { get; init; }
}

public interface IRefreshTokenStore
{
    Task<RefreshTokenRecord?> FindAsync(byte[] tokenHash, CancellationToken ct);

    Task InsertAsync(RefreshTokenRecord record, CancellationToken ct);

    Task RevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct);

    Task RevokeFamilyAsync(Guid familyId, DateTime revokedAt, CancellationToken ct);

    Task RevokeAllForUserAsync(Guid userId, DateTime revokedAt, CancellationToken ct);
}

public sealed record AccessToken(string Token, int ExpiresInSeconds);

public sealed record IssuedRefreshToken(string Token, DateTime ExpiresAt, Guid FamilyId);

public sealed record RefreshRotation(Guid UserId, IssuedRefreshToken Refresh);

/// <summary>
/// HS256 access tokens (claims: sub, email, name, hh, jti, iat, exp) and opaque refresh tokens
/// (32 random bytes, base64url) that are hashed at rest, rotated on every use and grouped in a
/// family; presenting an already-revoked token revokes the whole family.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options, IRefreshTokenStore store, TimeProvider time, ILogger<TokenService> logger)
{
    public const string HouseholdClaim = "hh";
    private const int RefreshTokenBytes = 32;

    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken CreateAccessToken(Guid userId, string email, string displayName, Guid? householdId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(_options.AccessMinutes);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Email] = email,
            [JwtRegisteredClaimNames.Name] = displayName,
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
        };
        if (householdId is { } hh)
        {
            claims[HouseholdClaim] = hh.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = JwtOptions.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_options.SigningKey, SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), (int)lifetime.TotalSeconds);
    }

    /// <summary>Issues a refresh token in a brand-new family (login, register, join, leave).</summary>
    public Task<IssuedRefreshToken> IssueRefreshTokenAsync(Guid userId, string? userAgent, CancellationToken ct) =>
        IssueAsync(userId, Guid.CreateVersion7(), userAgent, ct);

    /// <summary>
    /// Rotates a refresh token. Returns null when the token is unknown, expired or already used;
    /// in the "already used" case the entire family is revoked because the token was replayed.
    /// </summary>
    public async Task<RefreshRotation?> RotateAsync(string refreshToken, string? userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var record = await store.FindAsync(HashToken(refreshToken), ct);
        if (record is null)
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (record.RevokedAt is not null)
        {
            logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking family {FamilyId}", record.UserId, record.FamilyId);
            await store.RevokeFamilyAsync(record.FamilyId, now, ct);
            return null;
        }

        if (record.ExpiresAt <= now)
        {
            return null;
        }

        await store.RevokeAsync(record.Id, now, ct);
        var issued = await IssueAsync(record.UserId, record.FamilyId, userAgent, ct);
        return new RefreshRotation(record.UserId, issued);
    }

    /// <summary>Revokes a single refresh token (logout). Only tokens that belong to <paramref name="userId"/> are touched.</summary>
    public async Task RevokeAsync(string refreshToken, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var record = await store.FindAsync(HashToken(refreshToken), ct);
        if (record is null || record.UserId != userId || record.RevokedAt is not null)
        {
            return;
        }

        await store.RevokeAsync(record.Id, time.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>Revokes every refresh token of a user (password change).</summary>
    public Task RevokeAllAsync(Guid userId, CancellationToken ct) =>
        store.RevokeAllForUserAsync(userId, time.GetUtcNow().UtcDateTime, ct);

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private async Task<IssuedRefreshToken> IssueAsync(Guid userId, Guid familyId, string? userAgent, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));
        var expiresAt = now.AddDays(_options.RefreshDays);

        await store.InsertAsync(new RefreshTokenRecord
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = HashToken(token),
            FamilyId = familyId,
            ExpiresAt = expiresAt,
            UserAgent = Truncate(userAgent, 256),
            CreatedAt = now,
        }, ct);

        return new IssuedRefreshToken(token, expiresAt, familyId);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
