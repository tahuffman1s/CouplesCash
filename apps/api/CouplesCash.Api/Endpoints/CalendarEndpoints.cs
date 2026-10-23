using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class CalendarEndpoints
{
    public const int MaxRangeDays = 93;

    public static RouteGroupBuilder MapCalendar(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/calendar").WithTags("Calendar").RequireAuthorization().RequireHousehold();
        group.MapGet("", GetCalendar).WithSummary("Occurrences due in [from, to] (at most 93 days), sorted by date then name, plus the expenses dated in the range");
        return group;
    }

    private static async Task<Results<Ok<CalendarResponse>, ValidationProblem>> GetCalendar(
        ClaimsPrincipal principal,
        OccurrenceService occurrences,
        ExpenseRepository expenses,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        if (from is null)
        {
            return Problems.Validation("from", "Required (YYYY-MM-DD).");
        }

        if (to is null)
        {
            return Problems.Validation("to", "Required (YYYY-MM-DD).");
        }

        if (to < from)
        {
            return Problems.Validation("to", "Must be on or after 'from'.");
        }

        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxRangeDays)
        {
            return Problems.Validation("to", $"Range must be at most {MaxRangeDays} days.");
        }

        var householdId = principal.GetHouseholdId();
        var list = await occurrences.ListAsync(householdId, from.Value, to.Value, ct);
        var expenseRows = await expenses.ListAsync(householdId, from.Value, to.Value, ct);
        return TypedResults.Ok(new CalendarResponse(from.Value, to.Value, list, expenseRows.Select(e => e.ToDto()).ToList()));
    }
}
