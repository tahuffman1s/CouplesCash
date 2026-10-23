using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;

namespace CouplesCash.Api.Services;

/// <summary>Builds the <see cref="AuthResult"/> returned by register, login, refresh, join and leave.</summary>
public sealed class AuthSessionService(TokenService tokens, HouseholdRepository households)
{
    /// <summary>Starts a new refresh-token family and returns a full auth result.</summary>
    public async Task<AuthResult> StartAsync(UserRow user, string? userAgent, CancellationToken ct)
    {
        var refresh = await tokens.IssueRefreshTokenAsync(user.Id, userAgent, ct);
        return await BuildAsync(user, refresh, ct);
    }

    /// <summary>Builds an auth result around an already-rotated refresh token.</summary>
    public async Task<AuthResult> BuildAsync(UserRow user, IssuedRefreshToken refresh, CancellationToken ct)
    {
        var household = await households.GetForUserAsync(user.Id, ct);
        var access = tokens.CreateAccessToken(user.Id, user.Email, user.DisplayName, household?.Id);
        return new AuthResult(user.ToDto(), household, access.Token, access.ExpiresInSeconds, refresh.Token, refresh.ExpiresAt);
    }
}
