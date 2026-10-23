using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class ActivityEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;

    public static RouteGroupBuilder MapActivity(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/activity").WithTags("Activity").RequireAuthorization().RequireHousehold();
        group.MapGet("", List).WithSummary("Household activity feed, newest first; keyset-paginated with ?before=<createdAt>");
        return group;
    }

    private static async Task<Ok<ActivityPage>> List(
        ClaimsPrincipal principal,
        ActivityRepository activity,
        CancellationToken ct,
        DateTime? before = null,
        int limit = DefaultLimit)
    {
        var size = Math.Clamp(limit, 1, MaxLimit);
        var cursor = before is { } b ? DateTime.SpecifyKind(b.Kind == DateTimeKind.Local ? b.ToUniversalTime() : b, DateTimeKind.Utc) : (DateTime?)null;
        var rows = await activity.ListAsync(principal.GetHouseholdId(), cursor, size, ct);

        var page = rows.Take(size).Select(r => r.ToDto()).ToList();
        var nextBefore = rows.Count > size ? page[^1].CreatedAt : (DateTime?)null;
        return TypedResults.Ok(new ActivityPage(page, nextBefore));
    }
}
