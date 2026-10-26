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

public static class CoupleEndpoints
{
    public static RouteGroupBuilder MapCouple(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/couple").WithTags("Couple").RequireAuthorization();

        group.MapGet("", GetCouple)
            .WithSummary("The caller's household (with settings), or null");
        group.MapPost("/code", CreateCode).WithValidation<CoupleCodeRequest>()
            .WithSummary("Create a Couple Code (creates the household when the caller has none)");
        group.MapPost("/join", Join).WithValidation<JoinRequest>().RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Join a household with a typed code or QR payload");
        group.MapPost("/leave", Leave)
            .WithSummary("Leave the household (deleted when empty)");
        group.MapPatch("", UpdateSettings).RequireHousehold().WithValidation<HouseholdSettingsRequest>()
            .WithSummary("Update household name and settings (currency, week start, time zone, default account, pay period, weekly spending target)");

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
        NpgsqlDataSource db,
        UserRepository users,
        HouseholdRepository households,
        CoupleCodeRepository codes,
        ActivityService activity,
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

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            var redeemed = await codes.RedeemAsync(uow.Connection, userId, hash, time.Now(), ct);
            switch (redeemed.Status)
            {
                case RedeemStatus.NotFound:
                    return InvalidCode();
                case RedeemStatus.HouseholdFull:
                    return Problems.Conflict("Household is full", "This household already has two members.");
                case RedeemStatus.AlreadyMember:
                    return Problems.Conflict("Already in a household");
            }

            await activity.RecordAsync(uow, redeemed.HouseholdId!.Value, userId, "couple.joined", "household", redeemed.HouseholdId,
                ActivitySummaries.CoupleJoined(), new { userId }, ct, user.DisplayName);
            await uow.CommitAsync(ct);
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }

    private static async Task<Results<Ok<AuthResult>, ProblemHttpResult>> Leave(
        HttpRequest http,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        UserRepository users,
        HouseholdRepository households,
        ActivityService activity,
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

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.Unauthorized("Unknown user");
        }

        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            // Recorded before the membership row goes; if the household ends up empty the cascade removes the event too.
            await activity.RecordAsync(uow, householdId.Value, userId, "couple.left", "household", householdId,
                ActivitySummaries.CoupleLeft(), new { userId }, ct, user.DisplayName);
            await households.LeaveAsync(uow.Connection, householdId.Value, userId, time.Now(), ct);
            await uow.CommitAsync(ct);
        }

        return TypedResults.Ok(await sessions.StartAsync(user, http.UserAgent(), ct));
    }

    private static async Task<Results<Ok<HouseholdDto>, NotFound, ValidationProblem>> UpdateSettings(
        HouseholdSettingsRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        HouseholdRepository households,
        AccountRepository accounts,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        if (request.DefaultAccountId is { HasValue: true, Value: { } accountId } && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("defaultAccountId", "Unknown account.");
        }

        var update = new HouseholdSettingsUpdate(
            Name: request.Name.HasValue ? request.Name.Value?.Trim() : null,
            Currency: request.Currency.HasValue ? request.Currency.Value?.Trim().ToUpperInvariant() : null,
            WeekStart: request.WeekStart.HasValue ? request.WeekStart.Value : null,
            Timezone: request.Timezone.HasValue ? request.Timezone.Value?.Trim() : null,
            DefaultAccountId: request.DefaultAccountId,
            PayPeriod: request.PayPeriod.HasValue ? request.PayPeriod.Value : null,
            PayAnchorDate: request.PayAnchorDate,
            WeeklySpendCents: request.WeeklySpendCents,
            AutoIgnoreTransfers: request.AutoIgnoreTransfers.HasValue ? request.AutoIgnoreTransfers.Value : null);

        var changed = new List<string>();
        if (update.Currency is not null)
        {
            changed.Add("currency");
        }

        if (update.WeekStart is not null)
        {
            changed.Add("week start");
        }

        if (update.Timezone is not null)
        {
            changed.Add("time zone");
        }

        if (update.DefaultAccountId.HasValue)
        {
            changed.Add("default account");
        }

        if (update.PayPeriod is not null || update.PayAnchorDate.HasValue)
        {
            changed.Add("pay period");
        }

        if (update.WeeklySpendCents.HasValue)
        {
            changed.Add("weekly spending target");
        }

        if (update.AutoIgnoreTransfers is not null)
        {
            changed.Add("auto-ignore transfers");
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (!await households.UpdateSettingsAsync(uow.Connection, householdId, update, ct))
        {
            return TypedResults.NotFound();
        }

        var household = await households.GetAsync(uow.Connection, householdId, ct);
        if (household is null)
        {
            return TypedResults.NotFound();
        }

        var summary = update.Name is not null && changed.Count == 0
            ? ActivitySummaries.CoupleRenamed(household.Name)
            : ActivitySummaries.CoupleSettings(changed);
        var data = new
        {
            name = update.Name,
            currency = update.Currency,
            weekStart = update.WeekStart,
            timezone = update.Timezone,
            defaultAccountId = update.DefaultAccountId.HasValue ? update.DefaultAccountId.Value : null,
            payPeriod = update.PayPeriod,
            payAnchorDate = update.PayAnchorDate.HasValue ? update.PayAnchorDate.Value : null,
            weeklySpendCents = update.WeeklySpendCents.HasValue ? update.WeeklySpendCents.Value : null,
            autoIgnoreTransfers = update.AutoIgnoreTransfers,
        };
        await activity.RecordAsync(uow, householdId, userId, "couple.settings", "household", householdId, summary, data, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Ok(household);
    }

    private static ProblemHttpResult InvalidCode() =>
        Problems.NotFound("Invalid or expired code", "The code is wrong, has expired or was already used.");
}
