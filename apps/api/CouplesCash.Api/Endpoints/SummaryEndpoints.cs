using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class SummaryEndpoints
{
    public static RouteGroupBuilder MapSummary(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/summary").WithTags("Summary").RequireAuthorization().RequireHousehold();
        group.MapGet("", GetSummary).WithSummary("Dashboard numbers for a month (default: current); expenses count as paid outflow");
        return group;
    }

    private static async Task<Results<Ok<SummaryDto>, ValidationProblem>> GetSummary(
        ClaimsPrincipal principal,
        SummaryService summary,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        var today = time.Today();
        if (!SummaryService.TryParseMonth(month, today, out var monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var householdId = principal.GetHouseholdId();
        var data = await summary.LoadMonthAsync(householdId, monthStart, ct);
        return TypedResults.Ok(await summary.BuildAsync(householdId, data, today, ct));
    }
}
