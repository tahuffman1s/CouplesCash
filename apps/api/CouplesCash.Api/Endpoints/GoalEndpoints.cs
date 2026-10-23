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

public static class GoalEndpoints
{
    private const string DefaultColor = "#10b981";
    private const string DefaultIcon = "piggy-bank";

    public static RouteGroupBuilder MapGoals(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/goals").WithTags("Goals").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Active savings goals with progress, pace and projection");
        group.MapPost("", Create).WithValidation<GoalRequest>().WithSummary("Create a goal (linked to an account, or manual)");
        group.MapGet("/{id:guid}", Get).WithSummary("Get a goal with its history (≤ 90 points, oldest first)");
        group.MapPut("/{id:guid}", Update).WithValidation<GoalRequest>().WithSummary("Replace a goal");
        group.MapDelete("/{id:guid}", Archive).WithSummary("Archive (soft delete) a goal");
        group.MapPost("/{id:guid}/contribute", Contribute).WithValidation<ContributeRequest>()
            .WithSummary("Add to (or take from) a manual goal; 400 for account-linked goals");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<GoalDto>>> List(ClaimsPrincipal principal, GoalRepository goals, GoalService service, CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var rows = await goals.ListAsync(householdId, ct);
        return TypedResults.Ok(await service.ToDtosAsync(householdId, rows, ct));
    }

    private static async Task<Results<Ok<GoalDto>, NotFound>> Get(Guid id, ClaimsPrincipal principal, GoalRepository goals, GoalService service, CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var row = await goals.GetAsync(householdId, id, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(await service.ToDtoAsync(householdId, row, ct));
    }

    private static async Task<Results<Created<GoalDto>, ValidationProblem>> Create(
        GoalRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        GoalRepository goals,
        GoalService service,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (request.AccountId is { } accountId && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        GoalRow row;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            row = await goals.CreateAsync(uow.Connection, householdId, userId, ToFields(request), time.Now(), ct);
            await activity.RecordAsync(uow, householdId, userId, "goal.created", "goal", row.Id,
                ActivitySummaries.GoalCreated(row, currency), Data(row), ct, principal.GetDisplayName());
            await uow.CommitAsync(ct);
        }

        return TypedResults.Created($"/v1/goals/{row.Id}", await service.ToDtoAsync(householdId, row, ct));
    }

    private static async Task<Results<Ok<GoalDto>, NotFound, ValidationProblem>> Update(
        Guid id,
        GoalRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        GoalRepository goals,
        GoalService service,
        AccountRepository accounts,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await goals.GetAsync(householdId, id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        if (request.AccountId is { } accountId && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        GoalRow? row;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            row = await goals.UpdateAsync(uow.Connection, householdId, id, ToFields(request), time.Now(), ct);
            if (row is null)
            {
                return TypedResults.NotFound();
            }

            await activity.RecordAsync(uow, householdId, userId, "goal.updated", "goal", row.Id,
                ActivitySummaries.GoalUpdated(row), Data(row), ct, principal.GetDisplayName());
            await uow.CommitAsync(ct);
        }

        return TypedResults.Ok(await service.ToDtoAsync(householdId, row, ct));
    }

    private static async Task<Results<NoContent, NotFound>> Archive(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        GoalRepository goals,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await goals.GetAsync(uow.Connection, householdId, id, ct);
        if (row is null || !await goals.ArchiveAsync(uow.Connection, householdId, id, time.Now(), ct))
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "goal.deleted", "goal", row.Id,
            ActivitySummaries.GoalDeleted(row), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<GoalDto>, NotFound, ValidationProblem>> Contribute(
        Guid id,
        ContributeRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        GoalRepository goals,
        GoalService service,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var existing = await goals.GetAsync(householdId, id, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        if (existing.IsLinked)
        {
            return Problems.Validation("amountCents", "This goal tracks an account; update the account balance instead.");
        }

        if (existing.SavedCents + request.AmountCents < 0)
        {
            return Problems.Validation("amountCents", "Cannot take out more than has been saved.");
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        GoalRow? row;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            row = await goals.ContributeAsync(uow.Connection, householdId, id, userId, request.AmountCents, time.Now(), ct);
            if (row is null)
            {
                return TypedResults.NotFound();
            }

            await activity.RecordAsync(uow, householdId, userId, "goal.contributed", "goal", row.Id,
                ActivitySummaries.GoalContributed(row, request.AmountCents, currency),
                new { goalId = row.Id, amountCents = request.AmountCents, currentCents = row.CurrentCents, targetCents = row.TargetCents }, ct, principal.GetDisplayName());
            await uow.CommitAsync(ct);
        }

        return TypedResults.Ok(await service.ToDtoAsync(householdId, row, ct));
    }

    private static GoalFields ToFields(GoalRequest r) => new(
        r.Name.Trim(),
        r.TargetCents,
        r.TargetDate,
        r.AccountId,
        r.Color?.ToLowerInvariant() ?? DefaultColor,
        r.Icon ?? DefaultIcon);

    private static object Data(GoalRow g) => new { goalId = g.Id, targetCents = g.TargetCents, accountId = g.AccountId, currentCents = g.CurrentCents };
}
