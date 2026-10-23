using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class CategoryEndpoints
{
    public static RouteGroupBuilder MapCategories(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/categories").WithTags("Categories").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Categories ordered by sortOrder");
        group.MapPost("", Create).WithValidation<CategoryRequest>().WithSummary("Create a category");
        group.MapPut("/{id:guid}", Update).WithValidation<CategoryRequest>().WithSummary("Update a category");
        group.MapDelete("/{id:guid}", Delete).WithSummary("Delete a category (items keep running, uncategorized)");

        return group;
    }

    private static async Task<Ok<List<CategoryDto>>> List(ClaimsPrincipal principal, CategoryRepository categories, CancellationToken ct)
    {
        var rows = await categories.ListAsync(principal.GetHouseholdId(), ct);
        return TypedResults.Ok(rows.Select(c => c.ToDto()).ToList());
    }

    private static async Task<Created<CategoryDto>> Create(CategoryRequest request, ClaimsPrincipal principal, CategoryRepository categories, CancellationToken ct)
    {
        var row = await categories.CreateAsync(principal.GetHouseholdId(), request.Name.Trim(), request.Color.ToLowerInvariant(), request.Icon, request.SortOrder, ct);
        return TypedResults.Created($"/v1/categories/{row.Id}", row.ToDto());
    }

    private static async Task<Results<Ok<CategoryDto>, NotFound>> Update(Guid id, CategoryRequest request, ClaimsPrincipal principal, CategoryRepository categories, CancellationToken ct)
    {
        var row = await categories.UpdateAsync(principal.GetHouseholdId(), id, request.Name.Trim(), request.Color.ToLowerInvariant(), request.Icon, request.SortOrder, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, ClaimsPrincipal principal, CategoryRepository categories, CancellationToken ct) =>
        await categories.DeleteAsync(principal.GetHouseholdId(), id, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}
