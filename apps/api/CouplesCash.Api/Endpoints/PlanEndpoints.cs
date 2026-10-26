using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CouplesCash.Api.Endpoints;

/// <summary>v7 the Friday plan: the pay week's arithmetic, the payday checklist and the savings sweep tick.</summary>
public static class PlanEndpoints
{
    public static RouteGroupBuilder MapPlan(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/plan").WithTags("Plan").RequireAuthorization().RequireHousehold();
        group.MapGet("", Get)
            .WithSummary("The Friday plan for the pay week containing ?date (default today, household time zone): available now, bills / fun money / one-offs / income, left at the end, savings bills, payday checklist, sweep, setup flags");
        group.MapPost("/sweep/done", Done).WithValidation<SweepDoneRequest>()
            .WithSummary("Tick the week's savings sweep by hand ({ weekStart, actualCents? }; default = the planned amount) · 404 when that week has no frozen sweep");
        group.MapDelete("/sweep/done", Undo)
            .WithSummary("Untick the week's savings sweep ({ weekStart } in the body, or ?weekStart=); the bank-feed detection then leaves that week alone");
        return group;
    }

    private static async Task<Results<Ok<PlanDto>, NotFound>> Get(ClaimsPrincipal principal, PlanService plans, CancellationToken ct, DateOnly? date = null)
    {
        var plan = await plans.GetAsync(principal.GetHouseholdId(), date, ct);
        return plan is null ? TypedResults.NotFound() : TypedResults.Ok(plan);
    }

    private static async Task<Results<Ok<SweepDto>, ProblemHttpResult>> Done(SweepDoneRequest request, ClaimsPrincipal principal, FridayService friday, CancellationToken ct)
    {
        var outcome = await friday.MarkSweepDoneAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), request.WeekStart!.Value, request.ActualCents, ct);
        return outcome.Sweep is { } sweep ? TypedResults.Ok(sweep.ToDto()) : NoSweep();
    }

    private static async Task<Results<Ok<SweepDto>, ProblemHttpResult, ValidationProblem>> Undo(
        ClaimsPrincipal principal,
        FridayService friday,
        CancellationToken ct,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SweepDoneRequest? request = null,
        [FromQuery] DateOnly? weekStart = null)
    {
        if ((request?.WeekStart ?? weekStart) is not { } week)
        {
            return Problems.Validation("weekStart", "weekStart is required (YYYY-MM-DD, a payday).");
        }

        var outcome = await friday.UndoSweepAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), week, ct);
        return outcome.Sweep is { } sweep ? TypedResults.Ok(sweep.ToDto()) : NoSweep();
    }

    private static ProblemHttpResult NoSweep() =>
        Problems.NotFound("No sweep for that week", "The savings sweep is frozen at payday 00:00; that week has none (no bills account then, or not a payday).");
}
