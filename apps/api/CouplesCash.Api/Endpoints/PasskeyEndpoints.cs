using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

/// <summary>
/// WebAuthn passkeys. Registration routes need a signed-in user; login routes are anonymous and share the
/// strict <c>auth</c> rate limit. Everything except <c>/config</c> answers 503 <c>passkeys_not_configured</c>
/// unless the origin is https (or http://localhost).
/// </summary>
public static class PasskeyEndpoints
{
    public static RouteGroupBuilder MapPasskeys(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/auth/passkeys").WithTags("Passkeys");

        group.MapGet("/config", Config).AllowAnonymous().WithSummary("Whether passkeys are available and for which relying party id");

        var ceremonies = group.MapGroup("").RequirePasskeys();
        ceremonies.MapPost("/register/options", RegisterOptions).RequireAuthorization().WithValidation<PasskeyOptionsRequest>()
            .WithSummary("Start registering a passkey (PublicKeyCredentialCreationOptionsJSON)");
        ceremonies.MapPost("/register", Register).RequireAuthorization().WithValidation<PasskeyRegisterRequest>()
            .WithSummary("Finish registering a passkey (RegistrationResponseJSON)");
        ceremonies.MapGet("", List).RequireAuthorization().WithSummary("The caller's passkeys");
        ceremonies.MapDelete("/{id:guid}", Delete).RequireAuthorization().WithSummary("Remove a passkey");

        var anonymous = ceremonies.MapGroup("").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);
        anonymous.MapPost("/login/options", LoginOptions)
            .WithSummary("Start a discoverable-credential sign-in (PublicKeyCredentialRequestOptionsJSON, empty allowCredentials)");
        anonymous.MapPost("/login", Login).WithValidation<PasskeyLoginRequest>()
            .WithSummary("Finish signing in with a passkey (AuthenticationResponseJSON) → AuthResult");

        return group;
    }

    private static Ok<PasskeyConfigResponse> Config(WebAuthnOptions options) =>
        TypedResults.Ok(new PasskeyConfigResponse(options.Enabled, options.Enabled ? options.RelyingPartyId : null));

    private static async Task<Results<Ok<PasskeyOptionsResponse>, ProblemHttpResult>> RegisterOptions(
        PasskeyOptionsRequest request,
        ClaimsPrincipal principal,
        UserRepository users,
        PasskeyService passkeys,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(principal.GetUserId(), ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        var (challengeId, options) = await passkeys.BeginRegistrationAsync(user, ct);
        return TypedResults.Ok(new PasskeyOptionsResponse(challengeId, options));
    }

    private static async Task<Results<Created<PasskeyDto>, ValidationProblem>> Register(
        PasskeyRegisterRequest request,
        ClaimsPrincipal principal,
        PasskeyService passkeys,
        CancellationToken ct)
    {
        var outcome = await passkeys.CompleteRegistrationAsync(principal.GetUserId(), request.ChallengeId, request.Name.Trim(), request.Credential, ct);
        if (outcome.Value is null)
        {
            return Problems.Validation("credential", outcome.Error ?? "Passkey registration failed.");
        }

        return TypedResults.Created($"/v1/auth/passkeys/{outcome.Value.Id}", outcome.Value.ToDto());
    }

    private static async Task<Ok<List<PasskeyDto>>> List(ClaimsPrincipal principal, WebAuthnRepository store, CancellationToken ct)
    {
        var rows = await store.ListForUserAsync(principal.GetUserId(), ct);
        return TypedResults.Ok(rows.Select(r => r.ToDto()).ToList());
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, ClaimsPrincipal principal, WebAuthnRepository store, CancellationToken ct) =>
        await store.DeleteAsync(principal.GetUserId(), id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Ok<PasskeyOptionsResponse>> LoginOptions(PasskeyService passkeys, CancellationToken ct)
    {
        var (challengeId, options) = await passkeys.BeginLoginAsync(ct);
        return TypedResults.Ok(new PasskeyOptionsResponse(challengeId, options));
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Login(
        PasskeyLoginRequest request,
        HttpRequest http,
        PasskeyService passkeys,
        UserRepository users,
        AuthSessionService sessions,
        CancellationToken ct)
    {
        var outcome = await passkeys.CompleteLoginAsync(request.ChallengeId, request.Credential, ct);
        if (outcome.Value is null)
        {
            return Problems.Unauthorized("Passkey sign-in failed", outcome.Error);
        }

        var user = await users.FindByIdAsync(outcome.Value.UserId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Passkey sign-in failed");
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }
}
