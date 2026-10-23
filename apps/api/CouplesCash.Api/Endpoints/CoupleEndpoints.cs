using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class CoupleEndpoints
{
    public static RouteGroupBuilder MapCouple(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/couple").WithTags("Couple").RequireAuthorization();

        group.MapGet("", GetCouple)
            .WithSummary("The caller's household, or null");
        group.MapPost("/code", CreateCode).WithValidation<CoupleCodeRequest>()
            .WithSummary("Create a Couple Code (creates the household when the caller has none)");
        group.MapPost("/join", Join).WithValidation<JoinRequest>().RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Join a household with a typed code or QR payload");
        group.MapPost("/leave", Leave)
            .WithSummary("Leave the household (deleted when empty)");
        group.MapPatch("", Rename).RequireHousehold().WithValidation<RenameHouseholdRequest>()
            .WithSummary("Rename the household");

        return group;
    }

    private static async Task<Ok<HouseholdDto?>> GetCouple(ClaimsPrincipal principal, HouseholdRepository households, CancellationToken ct) =>
        TypedResults.Ok<HouseholdDto?>(await households.GetForUserAsync(principal.GetUserId(), ct));

    private static async Task<Results<Created<CoupleCodeResponse>, ProblemHttpResult>> CreateCode(
        CoupleCodeRequest? request,
        ClaimsPrincipal principal,
        UserRepository users,
        HouseholdRepository households,
        CoupleCodeRepository codes,
        CoupleCodeService codeService,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var now = time.Now();

        // Membership is read from the database here (not the hh claim): the caller may have just
        // created a household and not refreshed their token yet.
        var household = await households.GetForUserAsync(userId, ct);
        if (household is null)
        {
            var user = await users.FindByIdAsync(userId, ct);
            if (user is null)
            {
                return Problems.Unauthorized("Unknown user");
            }

            var name = request?.Name?.Trim() is { Length: > 0 } n ? n : $"{user.DisplayName}'s household";
            household = await households.CreateAsync(userId, name, now, ct);
        }
        else if (household.Members.Count >= 2)
        {
            return Problems.Conflict("Household is full", "This household already has two members.");
        }

        var code = codeService.Generate();
        var expiresAt = now.Add(CoupleCodeService.TimeToLive);
        await codes.CreateAsync(household.Id, userId, code, now, expiresAt, ct);

        return TypedResults.Created("/v1/couple", new CoupleCodeResponse(code.Code, code.Payload, expiresAt, household));
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Join(
        JoinRequest request,
        HttpRequest http,
        ClaimsPrincipal principal,
        UserRepository users,
        HouseholdRepository households,
        CoupleCodeRepository codes,
        AuthSessionService sessions,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        if (await households.GetHouseholdIdForUserAsync(userId, ct) is not null)
        {
            return Problems.Conflict("Already in a household", "Leave your current household before joining another.");
        }

        if (!CoupleCodeService.TryComputeLookupHash(request.Code, out var hash))
        {
            return InvalidCode();
        }

        var redeemed = await codes.RedeemAsync(hash, userId, time.Now(), ct);
        switch (redeemed.Status)
        {
            case RedeemStatus.NotFound:
                return InvalidCode();
            case RedeemStatus.HouseholdFull:
                return Problems.Conflict("Household is full", "This household already has two members.");
            case RedeemStatus.AlreadyMember:
                return Problems.Conflict("Already in a household");
        }

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Leave(
        HttpRequest http,
        ClaimsPrincipal principal,
        UserRepository users,
        HouseholdRepository households,
        AuthSessionService sessions,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var householdId = await households.GetHouseholdIdForUserAsync(userId, ct);
        if (householdId is null)
        {
            return Problems.NoHousehold();
        }

        await households.LeaveAsync(householdId.Value, userId, time.Now(), ct);

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }

    private static async Task<Results<Ok<HouseholdDto>, NotFound>> Rename(
        RenameHouseholdRequest request,
        ClaimsPrincipal principal,
        HouseholdRepository households,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        if (!await households.RenameAsync(householdId, request.Name.Trim(), ct))
        {
            return TypedResults.NotFound();
        }

        var household = await households.GetAsync(householdId, ct);
        return household is null ? TypedResults.NotFound() : TypedResults.Ok(household);
    }

    private static ProblemHttpResult InvalidCode() =>
        Problems.NotFound("Invalid or expired code", "The code is wrong, has expired or was already used.");
}
