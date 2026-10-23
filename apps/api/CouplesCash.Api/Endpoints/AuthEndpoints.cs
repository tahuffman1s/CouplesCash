using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuth(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/auth").WithTags("Auth");

        // Credential endpoints share the tight per-IP window.
        var credentials = group.MapGroup("").RequireRateLimiting(RateLimitPolicies.Auth);
        credentials.MapPost("/register", Register).AllowAnonymous().WithValidation<RegisterRequest>()
            .WithSummary("Create an account and sign in");
        credentials.MapPost("/login", Login).AllowAnonymous().WithValidation<LoginRequest>()
            .WithSummary("Sign in with email + password");
        credentials.MapPost("/refresh", Refresh).AllowAnonymous().WithValidation<RefreshRequest>()
            .WithSummary("Rotate the refresh token and get a new access token");
        credentials.MapPost("/logout", Logout).RequireAuthorization().WithValidation<LogoutRequest>()
            .WithSummary("Revoke a refresh token");
        credentials.MapPost("/password", ChangePassword).RequireAuthorization().WithValidation<ChangePasswordRequest>()
            .WithSummary("Change password (revokes every refresh token)");

        group.MapGet("/me", Me).RequireAuthorization()
            .WithSummary("Current user and household");
        group.MapPatch("/me", UpdateMe).RequireAuthorization().WithValidation<UpdateMeRequest>()
            .WithSummary("Update profile");

        return group;
    }

    private static async Task<Results<Created<AuthResult>, ProblemHttpResult>> Register(
        RegisterRequest request,
        HttpRequest http,
        UserRepository users,
        IPasswordHasher hasher,
        AuthSessionService sessions,
        TimeProvider time,
        CancellationToken ct)
    {
        var hash = hasher.Hash(request.Password);
        var user = await users.CreateAsync(request.Email.Trim(), request.DisplayName.Trim(), hash, time.Now(), ct);
        if (user is null)
        {
            return Problems.Conflict("Email already registered");
        }

        var result = await sessions.StartAsync(user, http.UserAgent(), ct);
        return TypedResults.Created("/v1/auth/me", result);
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Login(
        LoginRequest request,
        HttpRequest http,
        UserRepository users,
        IPasswordHasher hasher,
        AuthSessionService sessions,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim(), ct);
        if (user is null)
        {
            // Burn the same Argon2 work as a real verification so unknown emails are not faster.
            hasher.Verify(request.Password, hasher.DummyHash);
            return InvalidCredentials();
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            return InvalidCredentials();
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Refresh(
        RefreshRequest request,
        HttpRequest http,
        TokenService tokens,
        UserRepository users,
        AuthSessionService sessions,
        CancellationToken ct)
    {
        var rotation = await tokens.RotateAsync(request.RefreshToken, http.UserAgent(), ct);
        if (rotation is null)
        {
            return Problems.Unauthorized("Invalid refresh token", "The refresh token is unknown, expired or was already used.");
        }

        var user = await users.FindByIdAsync(rotation.UserId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Invalid refresh token");
        }

        return TypedResults.Ok(await sessions.BuildAsync(user, rotation.Refresh, ct));
    }

    private static async Task<NoContent> Logout(LogoutRequest request, ClaimsPrincipal principal, TokenService tokens, CancellationToken ct)
    {
        await tokens.RevokeAsync(request.RefreshToken, principal.GetUserId(), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> Me(
        ClaimsPrincipal principal,
        UserRepository users,
        HouseholdRepository households,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        var household = await households.GetForUserAsync(userId, ct);
        return TypedResults.Ok(new MeResponse(user.ToDto(), household));
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> UpdateMe(
        UpdateMeRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        UserRepository users,
        HouseholdRepository households,
        ActivityService activity,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        if (request.DisplayName is not { } name)
        {
            var current = await users.FindByIdAsync(userId, ct);
            return current is null ? Problems.Unauthorized("Unknown user") : TypedResults.Ok(current.ToDto());
        }

        // Membership from the database: the rename shows up in the partner's feed even with a stale token.
        var householdId = await households.GetHouseholdIdForUserAsync(userId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var user = await users.UpdateDisplayNameAsync(uow.Connection, userId, name.Trim(), ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        if (householdId is { } hh)
        {
            await activity.RecordAsync(uow, hh, userId, "user.renamed", "user", userId,
                ActivitySummaries.UserRenamed(user.DisplayName), new { userId, displayName = user.DisplayName }, ct, user.DisplayName);
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(user.ToDto());
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ChangePassword(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserRepository users,
        IPasswordHasher hasher,
        TokenService tokens,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Problems.Validation("currentPassword", "Current password is incorrect.");
        }

        await users.UpdatePasswordHashAsync(userId, hasher.Hash(request.NewPassword), ct);
        await tokens.RevokeAllAsync(userId, ct);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult InvalidCredentials() =>
        Problems.Unauthorized("Invalid email or password");
}
