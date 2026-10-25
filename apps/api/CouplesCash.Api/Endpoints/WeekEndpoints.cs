using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

/// <summary>v4 week view: planned items per day, left at end of week, weekly budgets.</summary>
public static class WeekEndpoints
{
    public static RouteGroupBuilder MapWeek(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/week").WithTags("Week").RequireAuthorization().RequireHousehold();
        group.MapGet("", Get).WithSummary("The week containing ?start (any date; default today in the household time zone): days, overdue, left at end of week, weekly budgets");
        return group;
    }

    private static async Task<Results<Ok<WeekViewDto>, NotFound>> Get(
        ClaimsPrincipal principal,
        WeekService weeks,
        CancellationToken ct,
        DateOnly? start = null)
    {
        var view = await weeks.GetAsync(principal.GetHouseholdId(), start, ct);
        return view is null ? TypedResults.NotFound() : TypedResults.Ok(view);
    }
}
